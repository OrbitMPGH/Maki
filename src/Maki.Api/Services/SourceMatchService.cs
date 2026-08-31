using System.Text.RegularExpressions;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

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
    /// Reads the search results' tracker ids and returns the one that is provably the same work,
    /// plus the ids of the ones that are provably not.
    /// </summary>
    /// <remarks>
    /// Costs nothing when the series carries no ids of its own (a title added before the metadata
    /// refresh filled them, or one MangaBaka has no cross-references for) — there is nothing to
    /// compare against, so no lookup is made.
    /// </remarks>
    private async Task<CrossIdVerdict> CrossIdPassAsync(
        ISource source, Series series, IReadOnlyList<SourceSeriesResult> results, CancellationToken ct)
    {
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var ours = ExternalIdsOf(series);
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
            .OrderByDescending(r => TitleScore(series, r.Title))
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
                        source.Name, result.Title, series.Title);
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
    private static double TitleScore(Series series, string candidateTitle)
    {
        var score = ScrobbleMatching.TitleSimilarity(series.Title, candidateTitle);
        var original = DisambiguatingOriginalTitle(series);
        return original is null
            ? score
            : Math.Max(score, ScrobbleMatching.TitleSimilarity(original, candidateTitle));
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
                    Enabled = true
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

    /// <returns>Names of sources that were automatically mapped.</returns>
    public async Task<List<string>> AutoMatchAsync(Series series, CancellationToken ct = default)
    {
        var mapped = new List<string>();

        // Cross-site ids gathered from confirmed matches, spent on the sources nothing matched.
        var crossRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var orderedSources = OrderSources(
            sourceRegistry.All, await settings.GetAsync(Maki.Core.Configuration.SettingKeys.SourcePriorityOrder, ct));
        var disabledSources = await sourceAvailability.DisabledAsync(ct);

        // Priority is the position in the *full* ordered list, so switching a source off
        // (or back on) never renumbers the mappings around it — and matches what
        // SourceMappingController assigns when a mapping is added by hand.
        foreach (var (source, priority) in orderedSources.Select((s, i) => (s, i + 1)))
        {
            if (disabledSources.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await db.SourceMappings.AnyAsync(m => m.SeriesId == series.Id && m.SourceName == source.Name, ct))
            {
                continue;
            }

            try
            {
                var results = await source.SearchAsync(series.Title, ct);
                var verdict = await CrossIdPassAsync(source, series, results, ct);
                var rejected = verdict.Rejected;

                SourceSeriesResult match;
                if (verdict.Confirmed is not null)
                {
                    match = verdict.Confirmed;
                    // Only a confirmed match's ids are worth acting on. A title match is as
                    // trustworthy as the title was, and seeding a second source off one would take a
                    // single wrong guess and write it into two mappings.
                    //
                    // Ids already held win a disagreement: sources are walked in priority order, so
                    // the first source to name a title is the one the user ranked highest.
                    crossRefs = SourceExternalIds.Merge(verdict.ConfirmedIds, crossRefs);
                    logger.LogInformation(
                        "Matched {Title} to {Source} ({SourceId}) by cross-site id",
                        series.Title, source.Name, match.SourceSeriesId);
                }
                else
                {
                    // A result the cross-id pass ruled out is a different work, whatever its title
                    // scores — which is the whole reason to run that pass before this one.
                    var usable = rejected.Count == 0
                        ? results
                        : results.Where(r => !rejected.Contains(r.SourceSeriesId)).ToList();

                    var candidates = usable
                        .Select(r => new ScrobbleCandidate(r.SourceSeriesId, r.Title, [], r.Url))
                        .ToList();
                    var best = ScrobbleMatching.BestCandidate(
                        series.Title, DisambiguatingOriginalTitle(series), candidates, MatchThreshold);
                    if (best is null)
                    {
                        continue;
                    }

                    match = usable.First(r => r.SourceSeriesId == best.Id);
                    logger.LogInformation("Auto-matched {Title} to {Source} ({SourceId})",
                        series.Title, source.Name, match.SourceSeriesId);
                }

                db.SourceMappings.Add(new SourceMapping
                {
                    SeriesId = series.Id,
                    SourceName = source.Name,
                    SourceSeriesId = match.SourceSeriesId,
                    Url = match.Url,
                    Priority = priority,
                    Enabled = true
                });
                mapped.Add(source.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Source search failed on {Source} for {Title}", source.Name, series.Title);
            }
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
