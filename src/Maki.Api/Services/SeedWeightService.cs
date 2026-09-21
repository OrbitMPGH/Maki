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
/// MangaBaka id -> seed weight, for the entries that carry one. A series rated 5 or better gets
/// <c>rating / 5.0</c> (10 → 2.0, 5 → 1.0 neutral); an unrated one gets whatever its reading
/// history implies, or no entry at all when there is no history to read. Anything absent is neutral
/// 1.0 by convention, so this is deliberately sparse rather than dense. A rating of 4 or under is
/// not a weak positive seed at all: it leaves this population and joins
/// <see cref="SeedSnapshot.Avoided"/>.
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
/// <param name="Avoided">
/// MangaBaka id -> avoidance strength in (0, 1]. Titles the reader thumbed down or rated at or
/// below <see cref="RecommendationFeedbackPolicy.AvoidRatingCeiling"/>. These are not seeds with a
/// negative weight: a negative weight in the centroid drags it to a point on the sphere that means
/// nothing. They are a separate channel the recommender subtracts with, so it is deliberately not
/// part of either <see cref="SeedWeights"/> population.
/// </param>
public record SeedSnapshot(
    SeedWeights Effective,
    SeedWeights Observed,
    IReadOnlyDictionary<long, SeriesReadSignal> Signals,
    IReadOnlyDictionary<long, double> Avoided)
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
            .Select(x => $"{x.Key}={x.Value.Completed}/{x.Value.Seconds}/{x.Value.LastReadAt?.Ticks}"))}" +
        $"|{string.Join(',', Avoided.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value:F4}"))}")))[..32];
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
/// <param name="catalogue">
/// The dump, for the one population whose content rating is not already on a library row: a manga
/// matched from an anime signal is usually not on the shelf at all, so its rating has to be read
/// from the catalogue before the ceiling can be applied to it. Optional because the seed builder
/// runs in tests and on instances with no dump, where there is nothing to read and the anime rows
/// are left as they were.
/// </param>
public class SeedWeightService(BehavioralTasteService taste, TasteTuning tuning, IAppSettings settings,
    MangaBakaLocalStore? catalogue = null)
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

        // Liked titles are mostly NOT library rows, which is the point of them: a rating can only
        // describe something already on the shelf. They join the effective seeds and nothing else —
        // not LibraryIds, which is the owned-candidate list, and not the observed population, which
        // describes the shelf. Ignoring a source still wins, so a reader can take one back.
        var liked = await RecommendationFeedbackService.LikedAsync(db, scope.UserId, ct);
        liked.ExceptWith(ignored);

        // The same argument, pulling the other way. A dislike is a whole-catalogue statement, so
        // most of these are not shelf rows either; a low rating can only ever be one.
        var disliked = await RecommendationFeedbackService.DislikedAsync(db, scope.UserId, ct);
        var avoided = new Dictionary<long, double>();
        foreach (var id in disliked.Except(ignored))
        {
            avoided[id] = 1.0;
        }

        foreach (var r in effectiveRows)
        {
            var strength = RecommendationFeedbackPolicy.AvoidStrength(r.Rating ?? 0);
            // Max, not overwrite. Both actions say the same thing, so a title that is both thumbed
            // down and rated 4 is pushed by the stronger of the two rather than by whichever the
            // reader happened to do second.
            if (strength > 0)
            {
                avoided[r.Id] = Math.Max(avoided.GetValueOrDefault(r.Id), strength);
            }
        }

        // A low rating leaves the positive population entirely rather than being filtered out of
        // the weights afterwards. Behavioural and personal-add weighting both fill in rows that
        // carry no rating, so a row still present here would come back as a positive seed on read
        // depth alone, which is the opposite of what the reader said. A thumbs down on a shelf title
        // says exactly the same thing, so it leaves the same way rather than steering and being
        // subtracted at once. The observed half keeps both, because it describes the shelf.
        var positiveRows = effectiveRows
            .Where(r => !avoided.ContainsKey(r.Id))
            .ToList();

        // Read over the wider population and narrow in memory: the effective ids are a subset, so a
        // second query would fetch the same progress rows only to throw some away.
        var signals = await taste.ReadSignalsAsync(db, scope.UserId, observedIds, ct);
        var behavioural = await TasteWeightingEnabledAsync(ct);
        var addWeighting =
            await settings.GetAsync(SettingKeys.RecommendationsPersonalAddWeighting, ct) != "false";
        var now = DateTime.UtcNow;

        var effectiveWeights = Weigh(positiveRows, signals, behavioural, addWeighting, now);
        // A liked title the reader then rated 2 is a contradiction they created, and the rating
        // wins: it is the considered action, same argument as LikedWeight's own remarks.
        foreach (var id in liked.Where(id => !avoided.ContainsKey(id)))
        {
            // Max, not overwrite: a liked title the reader also rated 10 keeps the rating's 2.0.
            effectiveWeights[id] = Math.Max(
                effectiveWeights.GetValueOrDefault(id, 1), RecommendationFeedbackPolicy.LikedWeight);
        }

        // Anime signals last, so every stronger kind of evidence is already in hand: a title on the
        // shelf, a thumbs up or down, and an ignored source all win over "the adaptation was good".
        var animeSeeds = new Dictionary<long, double>();
        if (await AnimeSignalsEnabledAsync(db, scope.UserId, ct))
        {
            var strength = await AnimeSignalStrengthAsync(db, scope.UserId, ct);
            // The reader's own evidence, which replaces an anime signal rather than adding to it.
            // Built from what this pass already has in hand; AnimeSignalsController asks the same
            // question with its own queries, against the same type, so the panel cannot describe a
            // rule the recommender is not applying.
            var precedence = new AnimeSignalPrecedence(
                libraryIds.ToHashSet(), liked.Concat(disliked).ToHashSet(), ignored);
            var animeRows = await db.AnimeSignals.AsNoTracking()
                .Where(x => x.UserId == scope.UserId && x.MangaBakaId != null)
                .Select(x => new AnimeSignalRow(
                    x.Service, x.AnimeId, x.MalAnimeId, x.Title, x.Score, x.Status, x.MangaBakaId))
                .ToListAsync(ct);

            // Grouped, never row by row. The raw rows hold one show once per tracker the reader
            // scrobbles to and one franchise once per season, so a per-row loop would read a single
            // opinion as several and let the best-liked season speak for a work the reader was
            // lukewarm on overall. One group is one manga, with the seasons' scores averaged.
            var groups = AnimeSignalGrouping.Group(animeRows).ToList();
            // The ceiling applies to a matched manga exactly as it applies to a shelf row, and the
            // only place a matched row's rating lives is the dump: nothing here is on the shelf, so
            // there is no ContentRating column to read it off. One batched read for every matched
            // id rather than a point query per group.
            var withinCeiling = await WithinCeilingAsync(
                groups.Where(g => g.MangaBakaId is not null).Select(g => g.MangaBakaId!.Value)
                    .Distinct().ToList(), allowed, ct);

            foreach (var group in groups)
            {
                if (group.MangaBakaId is not { } id || precedence.Supersedes(id) ||
                    withinCeiling is not null && !withinCeiling.Contains(id))
                {
                    continue;
                }

                var avoid = AnimeSignalPolicy.AvoidStrengthOf(group.Status, group.Score, strength);
                if (avoid > 0)
                {
                    avoided[id] = Math.Max(avoided.GetValueOrDefault(id), avoid);
                    animeSeeds.Remove(id);
                    continue;
                }

                var weight = AnimeSignalPolicy.SeedWeightOf(group.Status, group.Score, strength);
                if (weight > 0 && !avoided.ContainsKey(id))
                {
                    animeSeeds[id] = weight;
                }
            }

            foreach (var (id, weight) in animeSeeds)
            {
                effectiveWeights[id] = Math.Max(effectiveWeights.GetValueOrDefault(id, 0), weight);
            }
        }

        var positiveIds = positiveRows.Select(r => r.Id).Distinct().ToList();
        return new SeedSnapshot(
            new SeedWeights(libraryIds, effectiveWeights,
                positiveIds
                    .Concat(liked.Where(id => !avoided.ContainsKey(id)))
                    // Seeds and nothing else, exactly like a liked title: not LibraryIds, which is
                    // the owned-candidate list, and not the observed population, which describes a
                    // shelf these are not on. Being a seed is also what keeps a matched title out of
                    // the recommender's own output, since it excludes everything it steered by.
                    .Concat(animeSeeds.Keys)
                    .Distinct().Order().ToList()),
            // The shelf half keeps its low-rated rows: it describes what the reader owns, and a
            // profile chart that dropped everything they disliked would describe a different shelf.
            new SeedWeights(libraryIds, Weigh(observedRows, signals, behavioural, addWeighting, now), observedIds),
            signals,
            avoided);
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
    /// The subset of <paramref name="ids"/> the dump rates at or below the reader's ceiling, or null
    /// when there is no dump to ask. Null rather than an empty set so a missing catalogue leaves the
    /// anime rows alone instead of silently dropping every one of them.
    /// </summary>
    private async Task<HashSet<long>?> WithinCeilingAsync(
        IReadOnlyList<long> ids, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        if (catalogue is null || ids.Count == 0 || !await catalogue.IsAvailableAsync(ct))
        {
            return null;
        }

        return (await catalogue.GetByIdsAsync(ids, allowed, ct))
            .Select(hit => long.TryParse(hit.ProviderId, out var id) ? id : -1)
            .Where(id => id > 0)
            .ToHashSet();
    }

    /// <summary>
    /// Whether this user's watched anime may steer their recommendations: the instance switch and
    /// their own opt-in, which is off until they say otherwise.
    /// <para>
    /// Read off the caller's context rather than through <c>IUserSettingsStore</c> so this service
    /// keeps the constructor it has. Eight call sites build it by hand, and a new dependency for one
    /// boolean would be churn in all of them.
    /// </para>
    /// </summary>
    private async Task<bool> AnimeSignalsEnabledAsync(MakiDbContext db, int userId, CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.RecommendationsAnimeSignals, ct) == "false")
        {
            return false;
        }

        return await db.UserSettings.AsNoTracking()
            .Where(x => x.UserId == userId && x.Key == SettingKeys.RecommendationsAnimeSignalsEnabled)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(ct) == "true";
    }

    /// <summary>
    /// How much authority this reader lets their watch history carry. Read the same way and for the
    /// same reason as the opt-in above.
    /// <para>
    /// It reaches the pool cache without any help: the level changes the seed weights and the
    /// avoidance values, and both are already part of <see cref="SeedSnapshot.Fingerprint"/> and of
    /// the recommender's own cache key. Moving the dial invalidates the pool on the next request
    /// rather than sitting behind a twelve-hour hit.
    /// </para>
    /// </summary>
    private async Task<AnimeSignalStrength> AnimeSignalStrengthAsync(
        MakiDbContext db, int userId, CancellationToken ct) =>
        AnimeSignalPolicy.ParseStrength(await db.UserSettings.AsNoTracking()
            .Where(x => x.UserId == userId && x.Key == SettingKeys.RecommendationsAnimeSignalsStrength)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(ct));

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
