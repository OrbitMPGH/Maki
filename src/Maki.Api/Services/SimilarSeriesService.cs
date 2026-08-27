using System.Collections.Concurrent;
using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>
/// "More like this" for one series: the semantic recommender seeded by that series alone, for the
/// rail on the series page.
///
/// <para>
/// Deliberately <em>not</em> routed through <see cref="RecommendationService"/>, for the same reason
/// <c>SeriesController.Related</c> isn't: that service holds a single process-wide cached pool shared
/// with Discover's Recommended tab, so a per-series seed would evict it on every series page visit and
/// the next Discover load would pay seconds to rebuild it.
/// </para>
///
/// <para>
/// Semantic path only. The genre/tag fallback (<see cref="MangaBakaLocalStore.GetSimilarAsync"/>) is a
/// full scan of the ~3 GB dump — fine once behind Discover's 12-hour cache, not fine on a page load —
/// so an unbuilt index yields an empty rail instead, which is the same "supplementary section, stay
/// quiet" contract the related rail already has for a series with no MangaBaka id.
/// </para>
/// </summary>
public class SimilarSeriesService(
    SemanticRecommender semantic, IAppSettings settings, ILogger<SimilarSeriesService> logger)
{
    /// <summary>
    /// Matches <c>RecommendationService.CacheFor</c>. The dump only changes when a new one is
    /// imported, and the vector index is rebuilt out of band, so there is nothing to go stale faster.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    /// <summary>
    /// Deep enough that stripping the caller's owned titles still leaves a full rail, shallow enough
    /// that the pool is small to hold. Nothing pages through this, unlike Discover's.
    /// </summary>
    private const int PoolSize = 60;

    /// <summary>One entry per (series, content-rating ceiling) actually being looked at.</summary>
    private const int MaxEntries = 256;

    /// <summary>
    /// A small MMR nudge, because one seed pulls hardest on its own author and its own other volumes,
    /// which is a rail of near-copies.
    ///
    /// <para>
    /// It is the only single-seed adjustment left. There used to be a reduced Genre/Author weight
    /// vector beside it, compensating for a genre channel whose scale moved with how concentrated the
    /// seed set was; <see cref="SemanticRecommender.GetSimilarAsync"/> normalizes that channel now, so
    /// the rail asks for the same weights Discover does and the two surfaces stop disagreeing about
    /// the same seed. Measured over 500 single-seed requests graded against the held-out vote graph,
    /// lowering Author on its own was <em>worse</em> than leaving it alone (nDCG@40 0.114 against
    /// 0.115 at one seed, 0.062 against 0.071 at three), so nothing here replaces it.
    /// </para>
    ///
    /// <para>
    /// This dial measured neutral on those labels — near-duplicate suppression is invisible to a
    /// graph of "readers paired these two" — and is kept for the failure the labels cannot see.
    /// </para>
    /// </summary>
    private const double Diversity = 0.15;

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyList<MangaBakaRecommendation>? Results;
        public DateTime ComputedAt = DateTime.MinValue;
        public long LastUsedTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>
    /// The pool for one seed, computed at most once per <see cref="Ttl"/>; concurrent callers for the
    /// same key wait on the first rather than each starting their own scan.
    /// <para>
    /// Owned series are <b>not</b> excluded here and the caller must strip them itself. Library
    /// membership is per user, so folding it in would fragment the cache one way per person; leaving it
    /// out keeps a key that is shared by everyone with the same content-rating ceiling.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetAsync(
        long mangaBakaId, IReadOnlyList<string> allowedRatings, CancellationToken ct = default)
    {
        if (!semantic.IsReady())
        {
            return [];
        }

        // Read before the key so flipping the instance switch lands on the next page load rather
        // than waiting out a 12-hour entry. The rail honours the same setting the Discover panel does:
        // somebody who turns the channel off means everywhere, not just one surface.
        var coGraph = !string.Equals(
            await settings.GetAsync(SettingKeys.RecommendationsCoGraph, ct), "false",
            StringComparison.OrdinalIgnoreCase);
        var coRead = !string.Equals(
            await settings.GetAsync(SettingKeys.RecommendationsCoRead, ct), "false",
            StringComparison.OrdinalIgnoreCase);

        var entry = _entries.GetOrAdd(
            $"{mangaBakaId}|{string.Join(',', allowedRatings)}|g:{(coGraph ? 1 : 0)}|c:{(coRead ? 1 : 0)}",
            _ => new Entry());
        var now = DateTime.UtcNow;
        Volatile.Write(ref entry.LastUsedTicks, now.Ticks);

        if (IsFresh(entry, now))
        {
            return entry.Results!;
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            // Somebody else computed it while this call waited for the gate.
            if (IsFresh(entry, DateTime.UtcNow))
            {
                return entry.Results!;
            }

            var results = await semantic.GetSimilarAsync(
                [mangaBakaId],
                [],
                PoolSize,
                RecommendationFilters.None with { ContentRatings = allowedRatings },
                obscurity: 0,
                seedWeights: null,
                diversity: Diversity,
                coGraph: coGraph,
                coRead: coRead,
                ct: ct);

            entry.Results = results;
            entry.ComputedAt = DateTime.UtcNow;
            logger.LogInformation(
                "Computed {Count} similar series for MangaBaka {Id}", results.Count, mangaBakaId);
            Trim();
            return results;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static bool IsFresh(Entry entry, DateTime now) =>
        entry.Results is not null && now - entry.ComputedAt < Ttl;

    /// <summary>Evicts the least recently used entries once over capacity.</summary>
    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries
                     .OrderBy(kv => Volatile.Read(ref kv.Value.LastUsedTicks))
                     .Take(_entries.Count - MaxEntries)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }
}
