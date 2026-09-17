using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Maki.Api.Services;

/// <summary>
/// A user's library in the recommender's seed space, and how much each entry should steer it.
/// </summary>
/// <param name="LibraryIds">
/// Every MangaBaka id the caller's library maps to, ordered by id. Ordered because callers fold it
/// into a cache key, where row order arriving from SQLite would otherwise produce a different key
/// for an unchanged library.
/// </param>
/// <param name="Weights">
/// MangaBaka id -> seed weight, for the entries that carry one. A rated series gets
/// <c>rating / 5.0</c> (10 → 2.0, 5 → 1.0 neutral, 1 → 0.2); an unrated one gets whatever its
/// reading history implies, or no entry at all when there is no history to read. Anything absent
/// is neutral 1.0 by convention, so this is deliberately sparse rather than dense.
/// </param>
/// <param name="EligibleIds">
/// The subset of <paramref name="LibraryIds"/> this population may actually seed from, ordered the
/// same way. Full-incognito and over-ceiling titles are always out; whether ignored sources are out
/// depends on which half of a <see cref="SeedSnapshot"/> this is. Never the whole library: owned
/// titles stay excluded as candidates through <paramref name="LibraryIds"/>, which is a different
/// question and keeps its own list.
/// </param>
public record SeedWeights(IReadOnlyList<long> LibraryIds, IReadOnlyDictionary<long, double> Weights,
    IReadOnlyList<long> EligibleIds);

/// <summary>
/// Both answers a library read can give, off one pass.
/// </summary>
/// <param name="Effective">
/// What the recommender steers with: ignored sources removed. The seeds, and what the Lab means by
/// "what recommendations use".
/// </param>
/// <param name="Observed">
/// What the shelf actually holds, ignored sources included. What the profile charts describe, so
/// excluding a source from recommendations does not rewrite the reading history that explains it.
/// </param>
/// <param name="Signals">
/// Read signals over <see cref="Observed"/>. Both halves weigh from this one read, and callers that
/// need the raw read population rather than the weights take it from here instead of querying again.
/// </param>
public record SeedSnapshot(
    SeedWeights Effective,
    SeedWeights Observed,
    IReadOnlyDictionary<long, SeriesReadSignal> Signals)
{
    /// <summary>
    /// A digest of every input behind this snapshot, for callers that key a cache on it.
    /// <para>
    /// Hashed rather than spelled out because the inputs are the whole library, its weights and its
    /// read counts: a literal key is hundreds of kilobytes on a large shelf, and a dictionary
    /// compares it in full on every lookup.
    /// </para>
    /// </summary>
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{string.Join(',', Observed.LibraryIds)}|{string.Join(',', Observed.EligibleIds)}" +
        $"|{string.Join(',', Effective.EligibleIds)}" +
        $"|{string.Join(',', Effective.Weights.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value:F4}"))}" +
        $"|{string.Join(',', Observed.Weights.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value:F4}"))}" +
        $"|{string.Join(',', Signals.OrderBy(x => x.Key)
            .Select(x => $"{x.Key}={x.Value.Completed}/{x.Value.Seconds}/{x.Value.LastReadAt?.Ticks}"))}")))[..32];
}

/// <summary>
/// Builds the per-user seed weights the recommender steers with.
/// <para>
/// Extracted from <see cref="RecommendationService"/> rather than left inline because a second
/// caller now needs the identical number: the taste profile exists to explain the recommender, and
/// a profile computed from its own copy of this arithmetic would explain something else the first
/// time either side was tuned.
/// </para>
/// </summary>
public class SeedWeightService(BehavioralTasteService taste, TasteTuning tuning, IAppSettings settings)
{
    /// <param name="db">
    /// The caller's context, already narrowed with <c>db.Scope.SetUser</c>. Passed in rather than
    /// resolved so the library is read once per request: both this and
    /// <see cref="BehavioralTasteService"/> read it, and a second context would read it twice.
    /// </param>
    public async Task<SeedWeights> BuildAsync(
        MakiDbContext db, ICurrentUser scope, CancellationToken ct = default)
        => (await SnapshotAsync(db, scope, ct)).Effective;

    /// <summary>
    /// Both populations and the reading behind them, off one library read.
    /// <para>
    /// The Taste page needs the observed shelf and the effective seeds side by side, and the cache
    /// key it stores them under needs the same numbers again. Building that from repeated
    /// <see cref="BuildAsync"/> calls reads the library, the overrides and the read counts once per
    /// call, on warm hits included, which costs more than the cache saves.
    /// </para>
    /// </summary>
    /// <param name="db">
    /// The caller's context, already narrowed with <c>db.Scope.SetUser</c>. Passed in rather than
    /// resolved so the library is read once per request: both this and
    /// <see cref="BehavioralTasteService"/> read it, and a second context would read it twice.
    /// </param>
    public async Task<SeedSnapshot> SnapshotAsync(
        MakiDbContext db, ICurrentUser scope, CancellationToken ct = default)
    {
        var rows = await db.Series
            .Where(s => s.MangaBakaId != null)
            .Select(s => new
            {
                Id = (long)s.MangaBakaId!.Value,
                s.Incognito,
                s.ContentRating,
                Rating = db.UserSeriesStates
                    .Where(u => u.SeriesId == s.Id)
                    .Select(u => u.Rating)
                    .FirstOrDefault(),
                AddedAt = db.UserSeriesStates
                    .Where(u => u.SeriesId == s.Id)
                    .Select(u => u.AddedToLibraryAtUtc)
                    .FirstOrDefault(),
            })
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        var libraryIds = rows.Select(r => r.Id).Distinct().ToList();
        var ignored = (await db.RecommendationSignalOverrides.AsNoTracking()
            .Where(x => x.UserId == scope.UserId && x.IgnoreAsSeed)
            .Select(x => x.ProviderId).ToListAsync(ct)).ToHashSet();
        var allowed = ContentRating.Allowed(scope.MaxContentRating);

        // Visibility first and ignoring second, because they answer different questions: an
        // over-ceiling or fully-incognito title is not the reader's evidence at all, while an
        // ignored one is evidence they asked the recommender to stop steering by. The profile
        // charts keep the second and drop the first.
        var observedRows = rows
            .Where(r => r.Incognito != IncognitoMode.Full &&
                (r.ContentRating is null || allowed.Contains(r.ContentRating)))
            .Select(r => new SeedRow(r.Id, r.Rating, r.AddedAt))
            .ToList();
        var effectiveRows = observedRows.Where(r => !ignored.Contains(r.Id)).ToList();
        var observedIds = observedRows.Select(r => r.Id).Distinct().ToList();
        var effectiveIds = effectiveRows.Select(r => r.Id).Distinct().ToList();

        // Liked titles are mostly NOT library rows, which is the point of them: a rating can only
        // describe something already on the shelf. They join the effective seeds and nothing else —
        // not LibraryIds, which is the owned-candidate list, and not the observed population, which
        // describes the shelf. Ignoring a source still wins, so a reader can take one back.
        var liked = await RecommendationFeedbackService.LikedAsync(db, scope.UserId, ct);
        liked.ExceptWith(ignored);

        // Read over the wider population and narrow in memory: the effective ids are a subset, so a
        // second query would fetch the same progress rows only to throw some away.
        var signals = await taste.ReadSignalsAsync(db, scope.UserId, observedIds, ct);
        var behavioural = await TasteWeightingEnabledAsync(ct);
        var addWeighting =
            await settings.GetAsync(SettingKeys.RecommendationsPersonalAddWeighting, ct) != "false";
        var now = DateTime.UtcNow;

        var effectiveWeights = Weigh(effectiveRows, signals, behavioural, addWeighting, now);
        foreach (var id in liked)
        {
            // Max, not overwrite: a liked title the reader also rated 10 keeps the rating's 2.0.
            effectiveWeights[id] = Math.Max(
                effectiveWeights.GetValueOrDefault(id, 1), RecommendationFeedbackPolicy.LikedWeight);
        }

        return new SeedSnapshot(
            new SeedWeights(libraryIds, effectiveWeights,
                effectiveIds.Concat(liked.Except(effectiveIds)).Order().ToList()),
            new SeedWeights(libraryIds, Weigh(observedRows, signals, behavioural, addWeighting, now), observedIds),
            signals);
    }

    private Dictionary<long, double> Weigh(
        IReadOnlyList<SeedRow> population,
        IReadOnlyDictionary<long, SeriesReadSignal> signals,
        bool behavioural,
        bool addWeighting,
        DateTime now)
    {
        var ids = population.Select(r => r.Id).ToHashSet();
        var seedWeights = new Dictionary<long, double>();
        foreach (var r in population.Where(r => r.Rating is >= 1 and <= 10))
        {
            seedWeights[r.Id] = r.Rating!.Value / 5.0;
        }

        // At the shipped RatingBlendAlpha of 1 a rated seed keeps its rating weight untouched, so
        // this only ever fills in seeds the user never rated.
        if (behavioural)
        {
            foreach (var (id, weight) in taste.Weights(signals, ids))
            {
                seedWeights[id] = seedWeights.TryGetValue(id, out var rated)
                    ? TasteWeights.Blend(rated, weight, tuning)
                    : weight;
            }
        }

        if (addWeighting)
        {
            var ratedIds = population.Where(r => r.Rating is >= 1 and <= 10).Select(r => r.Id).ToHashSet();
            foreach (var r in population.Where(r => !ratedIds.Contains(r.Id) && r.AddedAt is not null))
            {
                var added = RecommendationFeedbackPolicy.AddedWeight(r.AddedAt!.Value, now);
                seedWeights[r.Id] = Math.Max(seedWeights.GetValueOrDefault(r.Id, 1), added);
            }
        }

        return seedWeights;
    }

    private sealed record SeedRow(long Id, int? Rating, DateTime? AddedAt);

    /// <summary>
    /// Whether behavioural seeding is on. Read per request rather than at startup so the switch takes
    /// effect on the next uncached pool instead of needing a restart; the read is one cached settings
    /// lookup, and the expensive part it guards is a full index scan.
    /// </summary>
    private async Task<bool> TasteWeightingEnabledAsync(CancellationToken ct)
    {
        if (tuning.IsUniform)
        {
            return false;
        }

        var value = await settings.GetAsync(SettingKeys.RecommendationsTasteWeighting, ct);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}
