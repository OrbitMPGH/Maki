using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Derives per-series seed weights from what a user has actually read, for the recommender to use on
/// seeds they never rated. Explicit ratings are sparse; completed chapters, time banked and how
/// recently they were read are not, and they say much the same thing.
/// <para>
/// Returns MangaBaka ids because that is the seed space the recommender works in — a library series
/// with no <c>MangaBakaId</c> cannot be a seed and so cannot carry a weight either. Series the user
/// has never opened produce no entry at all and stay at the implicit neutral weight: an unread
/// series is a backlog, not a dislike.
/// </para>
/// </summary>
public class BehavioralTasteService(TasteTuning tuning)
{
    /// <summary>
    /// Every series the user has actually read, as a raw signal, before the weight function and the
    /// neutral-drop in <see cref="WeightsAsync"/> run.
    /// <para>
    /// Split out because "read at all" and "read enough to move a seed" are different questions off
    /// the same query, and <c>TasteProfileService</c> needs the first: a series whose signal happens
    /// to land at neutral was still read, and belongs in the profile's read population. Sharing the
    /// query is what stops the two answers drifting on incognito or visibility rules.
    /// </para>
    /// </summary>
    /// <param name="db">
    /// The caller's context. Passed in rather than resolved because every caller already opened a
    /// child scope and narrowed it, and a second context would read the library twice.
    /// </param>
    /// <param name="userId">
    /// Whose reading to read. Explicit, because every query below bypasses the global filter — see
    /// <see cref="ReadCounts.ReadFor"/> for why that is the right shape here and why dropping the
    /// predicate would return everybody's rows rather than nobody's.
    /// </param>
    /// <param name="visibleIds">
    /// MangaBaka ids of the library as the caller can see it, already read under the scoped query.
    /// Intersecting against it is what puts root-folder visibility back, since the reading queries
    /// themselves run with filters off.
    /// </param>
    public async Task<IReadOnlyDictionary<long, SeriesReadSignal>> ReadSignalsAsync(
        MakiDbContext db, int userId, IReadOnlyCollection<long> visibleIds, CancellationToken ct = default)
    {
        if (userId <= 0 || visibleIds.Count == 0)
        {
            return new Dictionary<long, SeriesReadSignal>();
        }

        var read = await ReadCounts.ReadFor(db, userId)
            .GroupBy(p => p.SeriesId)
            .Select(g => new
            {
                SeriesId = g.Key,
                Completed = g.Count(),
                Seconds = g.Sum(p => (long)p.ReadSeconds),
                LastReadAt = g.Max(p => p.UpdatedAt),
            })
            .ToListAsync(ct);

        if (read.Count == 0)
        {
            return new Dictionary<long, SeriesReadSignal>();
        }

        var candidates = read.Select(r => r.SeriesId).ToList();

        var downloaded = await db.Chapters.IgnoreQueryFilters()
            .Where(c => candidates.Contains(c.SeriesId) && c.ChapterFileId != null)
            .GroupBy(c => c.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);

        // Fully-incognito series are dropped here rather than anywhere upstream: their ChapterProgress
        // rows exist (only the StatsEvents are suppressed), so this is the second aggregate that has
        // to write the gate out by hand — UserMetricsService.FullyReadAsync is the other one.
        // ScrobbleOnly is deliberately kept: it already counts in Rewind and read history.
        var mangaBakaIds = await db.Series.IgnoreQueryFilters()
            .Where(s => candidates.Contains(s.Id)
                        && s.MangaBakaId != null
                        && s.Incognito != IncognitoMode.Full)
            .Select(s => new { s.Id, MangaBakaId = (long)s.MangaBakaId!.Value })
            .ToDictionaryAsync(x => x.Id, x => x.MangaBakaId, ct);

        var visible = visibleIds as IReadOnlySet<long> ?? visibleIds.ToHashSet();
        var signals = new Dictionary<long, SeriesReadSignal>();

        foreach (var row in read)
        {
            if (!mangaBakaIds.TryGetValue(row.SeriesId, out var id) || !visible.Contains(id))
            {
                continue;
            }

            var signal = new SeriesReadSignal(
                row.Completed, downloaded.GetValueOrDefault(row.SeriesId), row.Seconds, row.LastReadAt);

            // MangaBakaId carries no unique index, so two local series can map to one catalogue entry
            // (a split release, a re-add that kept both rows). Keep the strongest evidence rather than
            // whichever row the scan happened to reach last, so the result does not depend on row order.
            // Ordered on the same channels the weight function reads first, so this picks the same row
            // the old Math.Max over the computed weights did.
            if (!signals.TryGetValue(id, out var existing) || Stronger(signal, existing))
            {
                signals[id] = signal;
            }
        }

        return signals;
    }

    /// <summary>
    /// Seed weights, keyed by MangaBaka id. Series whose reading implies nothing (a weight that
    /// rounds to <see cref="TasteWeights.Neutral"/>) are left out entirely: they would change no
    /// score, and leaving them in lengthens <c>RecommendationService</c>'s pool cache key for
    /// nothing.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, double>> WeightsAsync(
        MakiDbContext db, int userId, IReadOnlyCollection<long> visibleIds, CancellationToken ct = default)
    {
        if (tuning.IsUniform)
        {
            return new Dictionary<long, double>();
        }

        var signals = await ReadSignalsAsync(db, userId, visibleIds, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weights = new Dictionary<long, double>();

        foreach (var (id, signal) in signals)
        {
            var weight = TasteWeights.Weight(signal, today, tuning);
            if (Math.Abs(weight - TasteWeights.Neutral) < 1e-9)
            {
                continue; // nothing to say about this seed
            }

            weights[id] = weight;
        }

        return weights;
    }

    /// <summary>
    /// Which of two signals for the same catalogue entry is the better evidence. Depth first, then
    /// banked time, then recency — the order <see cref="TasteWeights.Weight"/> itself weights them.
    /// </summary>
    private static bool Stronger(SeriesReadSignal candidate, SeriesReadSignal existing)
    {
        if (candidate.Completed != existing.Completed)
        {
            return candidate.Completed > existing.Completed;
        }

        if (candidate.Seconds != existing.Seconds)
        {
            return candidate.Seconds > existing.Seconds;
        }

        return candidate.LastReadAt > existing.LastReadAt;
    }
}
