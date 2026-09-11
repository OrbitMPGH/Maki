using System.Text.RegularExpressions;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>Where one source has got to while <see cref="SourceMatchService"/> works through them.</summary>
public enum SourceMatchState
{
    /// <summary>Queued or in flight. Every source in the run reports this before anything else.</summary>
    Searching,

    /// <summary>A result was accepted. The mapping row itself is not written until the run ends.</summary>
    Matched,

    /// <summary>Nothing matched, or the source failed. Reported when that source settles.</summary>
    NoMatch
}

/// <summary>
/// One progress line from a source-matching run, for a caller that wants to show the sources
/// resolving one at a time rather than waiting for the whole run.
/// </summary>
public readonly record struct SourceMatchStep(string SourceName, SourceMatchState State);

/// <summary>
/// Tries to link a freshly added series to site sources by title search.
/// Only creates a mapping automatically when a search result's title similarity
/// (see <see cref="ScrobbleMatching"/>) reaches <see cref="MatchThreshold"/>;
/// anything fuzzier is left for the user to pick in the UI.
/// <para>
/// Before that, results are checked against the cross-site tracker ids we hold for the series (see
/// <see cref="SourceExternalIds"/>). Titles cannot separate two works that are named almost the same,
/// so where a source publishes the ids as well, identity is used instead: an agreeing id accepts a
/// result no matter how its title scores, and a result whose ids all disagree is thrown out before
/// the title pass ever sees it.
/// </para>
/// </summary>
public partial class SourceMatchService(
    MakiDbContext db,
    SourceRegistry sourceRegistry,
    Maki.Core.Configuration.IAppSettings settings,
    SourceAvailability sourceAvailability,
    SourceExternalIdCache externalIdCache,
    ILogger<SourceMatchService> logger)
{
    /// <summary>
    /// Lower than <see cref="ScrobbleMatching.MatchThreshold"/>: source search results
    /// legitimately include subtitle variants ("Hajime no Ippo" vs "...: Fighting Spirit!"),
    /// which score well below the scrobbling threshold meant for zero-review auto-accept.
    /// </summary>
    private const double MatchThreshold = 0.6;

    /// <summary>
    /// How many candidates are worth a cross-id lookup on a source that charges a page fetch for one.
    /// Search returns up to twenty hits and matching runs per source per series, so looking every hit
    /// up would turn adding one series into a burst of scrapes through that source's shared rate
    /// limiter. The lookups go to the best-scoring titles first, which is where the twin that fuzzy
    /// matching gets wrong actually sits. Sources whose search response already carries the ids are
    /// not subject to this — those are free, and every result is checked.
    /// </summary>
    private const int MaxExternalIdLookups = 3;

    /// <summary>
    /// How many sources are searched at once. Each has its own host and its own rate limiter, so
    /// this is not a politeness budget — searching them one at a time was costing the sum of every
    /// site's latency for no budget reason. It is a ceiling on how many scrapes, FlareSolverr solves
    /// and Playwright pages are in flight for one series at once, which is what keeps a match from
    /// starving the download workers that share those same singletons.
    /// </summary>
    private const int MaxParallelSources = 6;

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    public static string Normalize(string title) =>
        NonAlphanumeric().Replace(title.ToLowerInvariant(), string.Empty);

    /// <summary>
    /// series.OriginalTitle, unless it's just a generic franchise banner (a proper
    /// prefix of Title, e.g. "NARUTO" as the original title of "Naruto: The Seventh
    /// Hokage and the Scarlet Spring") - that's too generic to disambiguate and can
    /// exactly equal an unrelated sibling/parent series' title in search results.
    /// </summary>
    private static string? DisambiguatingOriginalTitle(Series series)
    {
        if (string.IsNullOrWhiteSpace(series.OriginalTitle))
        {
            return null;
        }

        var normalizedOriginal = Normalize(series.OriginalTitle);
        var normalizedTitle = Normalize(series.Title);
        var isGenericPrefix = normalizedOriginal.Length < normalizedTitle.Length
            && normalizedTitle.StartsWith(normalizedOriginal, StringComparison.Ordinal);

        return isGenericPrefix ? null : series.OriginalTitle;
    }

    /// <summary>
    /// Sources named in the "sources.priorityorder" CSV setting, in that order, followed by any
    /// remaining registered sources in registration order. Unknown names in the setting are ignored.
    /// </summary>
    public static List<ISource> OrderSources(IReadOnlyCollection<ISource> all, string? priorityCsv)
    {
        var preferred = (priorityCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s is not null)
            .Cast<ISource>()
            .ToList();

        return preferred.Concat(all.Where(s => !preferred.Contains(s))).ToList();
    }

    /// <summary>
    /// The cross-site tracker ids we hold for a series, from the columns MangaBaka fills.
    /// </summary>
    private static Dictionary<string, string> ExternalIdsOf(Series series) =>
        SourceExternalIds.From(
            (ExternalIdService.MangaBaka, series.MangaBakaId?.ToString()),
            (ExternalIdService.Mal, series.MalId?.ToString()),
            (ExternalIdService.AniList, series.AniListId?.ToString()),
            (ExternalIdService.Kitsu, series.KitsuId?.ToString()),
            (ExternalIdService.MangaUpdates, series.MangaUpdatesId),
            (ExternalIdService.MangaDex, series.MangaDexUuid));

    /// <summary>
    /// Everything the searches need about the series, copied out of the tracked entity before the
    /// fan-out starts. Sources are searched in parallel and an EF entity is not thread-safe, so no
    /// task is given the <see cref="Series"/> itself.
    /// </summary>
    private sealed record MatchTarget(
        string Title,
        string? OriginalTitle,
        IReadOnlyDictionary<string, string> ExternalIds)
    {
        public static MatchTarget For(Series series) =>
            new(series.Title, DisambiguatingOriginalTitle(series), ExternalIdsOf(series));
    }

    /// <summary>
    /// Reads the search results' tracker ids and returns the one that is provably the same work,
    /// plus the ids of the ones that are provably not.
    /// </summary>
    /// <remarks>
    /// Costs nothing when the series carries no ids of its own (a title added before the metadata
    /// refresh filled them, or one MangaBaka has no cross-references for) — there is nothing to
    /// compare against, so no lookup is made.
    /// </remarks>
    private async Task<CrossIdVerdict> CrossIdPassAsync(
        ISource source, MatchTarget target, IReadOnlyList<SourceSeriesResult> results, CancellationToken ct)
    {
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var ours = target.ExternalIds;
        if (ours.Count == 0 || results.Count == 0)
        {
            return new CrossIdVerdict(null, null, rejected);
        }

        // Results whose ids arrived with the search itself. No request to make, so every one is read.
        foreach (var result in results)
        {
            switch (SourceExternalIds.Compare(ours, result.ExternalIds))
            {
                case ExternalIdVerdict.Match:
                    return new CrossIdVerdict(result, result.ExternalIds, rejected);
                case ExternalIdVerdict.Mismatch:
                    rejected.Add(result.SourceSeriesId);
                    break;
            }
        }

        // Anything the inline ids settled is already out (a match returned, a mismatch is in the set).
        // Carrying *some* ids is not a reason to skip the lookup: a source can publish one set with
        // its search results and a different one on the series page, and Atsumaru does exactly that.
        // A source with no lookup to make answers the default instantly and costs nothing.
        var lookups = results
            .Where(r => !rejected.Contains(r.SourceSeriesId))
            .OrderByDescending(r => TitleScore(target, r.Title))
            .Take(MaxExternalIdLookups)
            .ToList();

        foreach (var result in lookups)
        {
            IReadOnlyDictionary<string, string>? theirs;
            try
            {
                theirs = await externalIdCache.GetAsync(source, result.SourceSeriesId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One candidate's page failing says nothing about the others and nothing about the
                // title pass, which can still match. Swallowing it here rather than letting it reach
                // the caller's catch is what keeps a single bad page from skipping the whole source.
                logger.LogDebug(ex, "Cross-id lookup failed on {Source} for {SourceId}",
                    source.Name, result.SourceSeriesId);
                continue;
            }

            switch (SourceExternalIds.Compare(ours, theirs))
            {
                case ExternalIdVerdict.Match:
                    return new CrossIdVerdict(
                        result, SourceExternalIds.Merge(result.ExternalIds, theirs), rejected);
                case ExternalIdVerdict.Mismatch:
                    logger.LogDebug(
                        "Ruled out {Source} result '{Result}' for {Title}: cross-site ids disagree",
                        source.Name, result.Title, target.Title);
                    rejected.Add(result.SourceSeriesId);
                    break;
            }
        }

        return new CrossIdVerdict(null, null, rejected);
    }

    /// <summary>
    /// What the cross-id pass decided for one source: the result proven to be the same work (with
    /// every id we saw for it), and the results proven not to be.
    /// </summary>
    private sealed record CrossIdVerdict(
        SourceSeriesResult? Confirmed,
        IReadOnlyDictionary<string, string>? ConfirmedIds,
        HashSet<string> Rejected);

    /// <summary>
    /// How close a result's title is to the series, used only to decide which candidates are worth
    /// spending a lookup on. There is no floor: a result scoring near zero is exactly the case
    /// cross-ids exist to rescue, so the score orders the queue rather than filtering it.
    /// </summary>
    private static double TitleScore(MatchTarget target, string candidateTitle)
    {
        var score = ScrobbleMatching.TitleSimilarity(target.Title, candidateTitle);
        return target.OriginalTitle is null
            ? score
            : Math.Max(score, ScrobbleMatching.TitleSimilarity(target.OriginalTitle, candidateTitle));
    }

    /// <summary>
    /// Maps the sources that nothing matched, using ids a confirmed match already handed us.
    /// </summary>
    /// <remarks>
    /// Some sites link their entries to other sites we download from — MangaFire names the MangaDex
    /// title, Atsumaru's search index names the WeebCentral entry — so once one source is confirmed,
    /// its neighbours' series ids are simply known. That is strictly better than searching for them:
    /// no title is involved, so the failure mode fuzzy matching has (a near-identical title on the
    /// wrong work) cannot happen, and a source whose search is weak or whose entry is titled in
    /// another language gets mapped anyway.
    /// <para>
    /// It runs after the search loop rather than during it, so a source always gets to find its own
    /// entry first: its own result is canonical (WeebCentral's own ids carry the slug, the borrowed
    /// one is the bare ULID), and doing it the other way round would need the loop's "already mapped"
    /// check to see rows that have not been saved yet.
    /// </para>
    /// <para>
    /// Deliberately does not write the id back to the Series columns. <c>SeriesIdentity.For</c> ranks
    /// MangaDex above AniList/MAL, so filling <c>MangaDexUuid</c> on a series that had none would
    /// change the key its whole stats history is written under, and that history does not move.
    /// </para>
    /// </remarks>
    private async Task<List<string>> SeedFromCrossRefsAsync(
        Series series,
        List<ISource> orderedSources,
        IReadOnlyCollection<string> disabledSources,
        List<string> alreadyMapped,
        IReadOnlyDictionary<string, string> crossRefs,
        CancellationToken ct)
    {
        var seeded = new List<string>();
        if (crossRefs.Count == 0)
        {
            return seeded;
        }

        foreach (var (source, priority) in orderedSources.Select((s, i) => (s, i + 1)))
        {
            if (!ExternalIdService.SourceSeriesIdServices.Contains(source.Name) ||
                !crossRefs.TryGetValue(source.Name, out var sourceSeriesId) ||
                alreadyMapped.Contains(source.Name) ||
                disabledSources.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await db.SourceMappings.AnyAsync(m => m.SeriesId == series.Id && m.SourceName == source.Name, ct))
            {
                continue;
            }

            try
            {
                // One call, which both proves the id still resolves — sites delete entries, and a
                // dead mapping would only surface later as a failed chapter sync — and gives the
                // canonical series URL, which there is no other way to build from an id here.
                var detail = await source.GetSeriesAsync(sourceSeriesId, ct);

                db.SourceMappings.Add(new SourceMapping
                {
                    SeriesId = series.Id,
                    SourceName = source.Name,
                    SourceSeriesId = detail.SourceSeriesId,
                    Url = detail.Url,
                    Priority = priority,
                    Enabled = true,
                    Origin = SourceMappingOrigin.CrossId
                });
                seeded.Add(source.Name);
                logger.LogInformation(
                    "Linked {Title} to {Source} ({SourceId}) from another source's cross-reference",
                    series.Title, source.Name, detail.SourceSeriesId);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Cross-reference to {Source} ({SourceId}) did not resolve for {Title}",
                    source.Name, sourceSeriesId, series.Title);
            }
        }

        return seeded;
    }

    /// <summary>What one source's search came back with. Carries no DbContext state on purpose.</summary>
    private sealed record SourceOutcome(
        ISource Source,
        int Priority,
        SourceSeriesResult? Match,
        SourceMappingOrigin Origin,
        IReadOnlyDictionary<string, string>? ConfirmedIds);

    /// <summary>
    /// Searches one source and decides what it matched, touching nothing shared. Never throws: a
    /// site being down says nothing about the other twelve, and letting the exception out would
    /// cancel the whole fan-out rather than the one source that failed.
    /// </summary>
    private async Task<SourceOutcome> SearchOneAsync(
        ISource source,
        int priority,
        MatchTarget target,
        IProgress<SourceMatchStep>? progress,
        CancellationToken ct)
    {
        var nothing = new SourceOutcome(source, priority, null, SourceMappingOrigin.TitleSearch, null);
        try
        {
            var results = await source.SearchAsync(target.Title, ct);
            var verdict = await CrossIdPassAsync(source, target, results, ct);

            if (verdict.Confirmed is not null)
            {
                progress?.Report(new SourceMatchStep(source.Name, SourceMatchState.Matched));
                return new SourceOutcome(
                    source, priority, verdict.Confirmed, SourceMappingOrigin.CrossId, verdict.ConfirmedIds);
            }

            // A result the cross-id pass ruled out is a different work, whatever its title
            // scores — which is the whole reason to run that pass before this one.
            var usable = verdict.Rejected.Count == 0
                ? results
                : results.Where(r => !verdict.Rejected.Contains(r.SourceSeriesId)).ToList();

            var candidates = usable
                .Select(r => new ScrobbleCandidate(r.SourceSeriesId, r.Title, [], r.Url))
                .ToList();
            var best = ScrobbleMatching.BestCandidate(
                target.Title, target.OriginalTitle, candidates, MatchThreshold);
            if (best is null)
            {
                progress?.Report(new SourceMatchStep(source.Name, SourceMatchState.NoMatch));
                return nothing;
            }

            progress?.Report(new SourceMatchStep(source.Name, SourceMatchState.Matched));
            return new SourceOutcome(
                source, priority, usable.First(r => r.SourceSeriesId == best.Id),
                SourceMappingOrigin.TitleSearch, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Source search failed on {Source} for {Title}", source.Name, target.Title);
            progress?.Report(new SourceMatchStep(source.Name, SourceMatchState.NoMatch));
            return nothing;
        }
    }

    /// <summary>Runs <paramref name="work"/> once the gate has room, and always gives the slot back.</summary>
    private static async Task<T> WithGate<T>(SemaphoreSlim gate, Func<Task<T>> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <param name="progress">
    /// Optional per-source running commentary, for a caller that shows the sources resolving one at
    /// a time. Every source reports <see cref="SourceMatchState.Searching"/> before the fan-out and
    /// reports <see cref="SourceMatchState.Matched"/> or <see cref="SourceMatchState.NoMatch"/> as
    /// soon as its own search settles. A later cross-reference seeding pass can still turn a
    /// <see cref="SourceMatchState.NoMatch"/> into a mapping, so the finished table remains
    /// authoritative.
    /// </param>
    /// <returns>Names of sources that were automatically mapped.</returns>
    public async Task<List<string>> AutoMatchAsync(
        Series series, CancellationToken ct = default, IProgress<SourceMatchStep>? progress = null)
    {
        var mapped = new List<string>();

        // Cross-site ids gathered from confirmed matches, spent on the sources nothing matched.
        var crossRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var orderedSources = OrderSources(
            sourceRegistry.All, await settings.GetAsync(Maki.Core.Configuration.SettingKeys.SourcePriorityOrder, ct));
        var disabledSources = await sourceAvailability.DisabledAsync(ct);

        // One read for every source instead of one each: the searches below run in parallel and are
        // not allowed near the DbContext. A stale answer here only ever costs a wasted search — the
        // authoritative guard is the re-check in the apply loop, which runs back on this thread.
        var alreadyMapped = new HashSet<string>(
            await db.SourceMappings
                .Where(m => m.SeriesId == series.Id)
                .Select(m => m.SourceName)
                .ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

        // Priority is the position in the *full* ordered list, so switching a source off
        // (or back on) never renumbers the mappings around it — and matches what
        // SourceMappingController assigns when a mapping is added by hand.
        var work = orderedSources
            .Select((source, index) => (Source: source, Priority: index + 1))
            .Where(item => !disabledSources.Contains(item.Source.Name, StringComparer.OrdinalIgnoreCase)
                           && !alreadyMapped.Contains(item.Source.Name))
            .ToList();

        var target = MatchTarget.For(series);
        foreach (var (source, _) in work)
        {
            progress?.Report(new SourceMatchStep(source.Name, SourceMatchState.Searching));
        }

        // Every source has its own host and its own rate limiter, so waiting for one before starting
        // the next was costing the sum of every site's latency for nothing.
        using var gate = new SemaphoreSlim(MaxParallelSources, MaxParallelSources);
        var outcomes = await Task.WhenAll(work.Select(item => WithGate(
            gate, () => SearchOneAsync(item.Source, item.Priority, target, progress, ct), ct)));

        // Back in priority order and back on one thread. Everything from here is order-sensitive:
        // the highest-ranked source has to win a cross-reference disagreement, the mappings have to
        // come out in priority order, and the DbContext is single-threaded.
        foreach (var outcome in outcomes.OrderBy(o => o.Priority))
        {
            if (outcome.Match is null)
            {
                continue;
            }

            // The snapshot above is a few seconds old by now; a source linked by hand while the
            // searches ran would otherwise collide on (SeriesId, SourceName) and fail the batch.
            if (await db.SourceMappings.AnyAsync(
                    m => m.SeriesId == series.Id && m.SourceName == outcome.Source.Name, ct))
            {
                continue;
            }

            if (outcome.Origin == SourceMappingOrigin.CrossId)
            {
                // Only a confirmed match's ids are worth acting on. A title match is as
                // trustworthy as the title was, and seeding a second source off one would take a
                // single wrong guess and write it into two mappings.
                //
                // Ids already held win a disagreement: outcomes are applied in priority order, so
                // the first source to name a title is the one the user ranked highest.
                crossRefs = SourceExternalIds.Merge(outcome.ConfirmedIds, crossRefs);
                logger.LogInformation(
                    "Matched {Title} to {Source} ({SourceId}) by cross-site id",
                    series.Title, outcome.Source.Name, outcome.Match.SourceSeriesId);
            }
            else
            {
                logger.LogInformation("Auto-matched {Title} to {Source} ({SourceId})",
                    series.Title, outcome.Source.Name, outcome.Match.SourceSeriesId);
            }

            db.SourceMappings.Add(new SourceMapping
            {
                SeriesId = series.Id,
                SourceName = outcome.Source.Name,
                SourceSeriesId = outcome.Match.SourceSeriesId,
                Url = outcome.Match.Url,
                Priority = outcome.Priority,
                Enabled = true,
                Origin = outcome.Origin
            });
            mapped.Add(outcome.Source.Name);
        }

        mapped.AddRange(await SeedFromCrossRefsAsync(series, orderedSources, disabledSources, mapped, crossRefs, ct));

        if (mapped.Count == 0)
        {
            return mapped;
        }

        // Matching every source is a series of network searches and can run for a minute; deleting
        // the series in the meantime leaves these mappings pointing at a row that is gone, and the
        // insert dies on the foreign key. There is nothing left to link, so drop them quietly.
        if (!await db.Series.AnyAsync(s => s.Id == series.Id, ct))
        {
            foreach (var entry in db.ChangeTracker.Entries<SourceMapping>()
                         .Where(e => e.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            logger.LogInformation("Series {Id} was deleted during source matching; dropping {Count} match(es)",
                series.Id, mapped.Count);
            mapped.Clear();
            return mapped;
        }

        await db.SaveChangesAsync(ct);

        return mapped;
    }
}
