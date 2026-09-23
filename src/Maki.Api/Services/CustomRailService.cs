using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One custom rail's rows. Catalogue and recommendation rails fill <see cref="Items"/>; a library
/// rail fills <see cref="SeriesIds"/>, which the client renders from the series list it already has.
/// </summary>
/// <param name="Unavailable">
/// Set instead of rows when the source cannot answer: <c>catalogue</c> when the local MangaBaka
/// database is off or missing. A code, worded by the client.
/// </param>
public record CustomRailItems(
    string Source,
    IReadOnlyList<MangaBakaRecommendation> Items,
    IReadOnlyList<int> SeriesIds,
    string? Unavailable = null);

/// <summary>
/// Resolves a <see cref="CustomRailSpec"/> into rows for the caller. Every source reuses the path the
/// matching Discover surface takes, so a rail shows what that surface would for the same settings:
/// the recommender for <c>recommendations</c>, the catalogue browse for <c>catalogue</c>. Library
/// rails evaluate the same filter over the caller's own series.
/// </summary>
public class CustomRailService(
    MakiDbContext db,
    ICurrentUser user,
    RecommendationService recommendations,
    DiscoverService discover,
    HiddenContentService hidden,
    RecommendationFeedbackService feedback,
    MangaBakaLocalStore store,
    VectorIndexCache vectorIndex)
{
    /// <summary>How many series the "read" sort looks back over, the same bound Home's reading rails use.</summary>
    private const int RecentProgressScan = 2000;

    public const string CatalogueUnavailable = "catalogue";

    public async Task<CustomRailItems> ItemsAsync(CustomRailSpec spec, int limit, CancellationToken ct = default)
    {
        spec = spec.Normalize();
        switch (spec.Source)
        {
            case CustomRailSources.Library:
                return new CustomRailItems(spec.Source, [], (await LibraryAsync(spec, ct)).Take(limit).ToList());

            case CustomRailSources.Recommendations:
                if (!await store.IsAvailableAsync(ct))
                {
                    return new CustomRailItems(spec.Source, [], [], CatalogueUnavailable);
                }

                var filters = await hidden.ApplyAsync(HiddenContentService.Sanitize(RecommendationFilters.FromSpec(spec.Filters)), ct);
                var seeds = spec.Seeds is { Count: > 0 } chosen ? chosen.Select(s => (long)s.Id).ToList() : null;
                var result = await recommendations.GetAsync(
                    new RecommendationRequest(seeds, filters, spec.Obscurity, Diversity: spec.Diversity),
                    user, ct, PoolOrigin.Rail);
                return new CustomRailItems(spec.Source, result.Similar.Take(limit).ToList(), []);

            default:
                try
                {
                    var scoped = await hidden.ScopeAsync(RecommendationFilters.FromSpec(spec.Filters), user.MaxContentRating, ct);
                    var exclude = await ExclusionsAsync(spec.ExcludeOwned, ct);
                    var items = await discover.GetFeedAsync(
                        new DiscoverFeedRequest(nameof(BrowseFeed.Popular), Filters: scoped, Limit: limit,
                            Sort: spec.Sort ?? BrowseSort.Popular),
                        ct, exclude);
                    return new CustomRailItems(spec.Source, items, []);
                }
                catch (InvalidOperationException)
                {
                    return new CustomRailItems(spec.Source, [], [], CatalogueUnavailable);
                }
        }
    }

    /// <summary>
    /// How many rows the rail could show, for the editor's live count. Null when there is no way to
    /// tell: the catalogue sources need the search index for that.
    /// </summary>
    public async Task<int?> CountAsync(CustomRailSpec spec, CancellationToken ct = default)
    {
        spec = spec.Normalize();
        if (spec.Source == CustomRailSources.Library)
        {
            return (await LibraryAsync(spec, ct)).Count;
        }

        var scoped = await hidden.ScopeAsync(RecommendationFilters.FromSpec(spec.Filters), user.MaxContentRating, ct);
        // The recommender never returns an owned title, so its count leaves them out too.
        var exclude = await ExclusionsAsync(spec.ExcludeOwned || spec.Source == CustomRailSources.Recommendations, ct);
        return await discover.CountAsync(new DiscoverFeedRequest(nameof(BrowseFeed.Popular), Filters: scoped), ct, exclude);
    }

    /// <summary>
    /// MangaBaka ids the caller should not be shown in a catalogue listing: whatever they hid or
    /// dismissed, plus their own series when <paramref name="excludeOwned"/> is set. Folded into the
    /// scan rather than filtered off the page, so a rail is never cut short by it.
    /// </summary>
    public async Task<HashSet<long>> ExclusionsAsync(bool excludeOwned, CancellationToken ct = default)
    {
        var excluded = await feedback.SuppressedAsync(user.UserId, ct);
        if (excludeOwned)
        {
            excluded.UnionWith(await db.Series
                .AsNoTracking()
                .Where(s => s.MangaBakaId != null)
                .Select(s => (long)s.MangaBakaId!.Value)
                .ToListAsync(ct));
        }

        return excluded;
    }

    /// <summary>
    /// The caller's series that pass the filter, ordered. No content ceiling or never-show list:
    /// those are Discover settings, and what a reader may see of their own library is decided by
    /// root folder, which the query filter already applies.
    /// </summary>
    private async Task<IReadOnlyList<int>> LibraryAsync(CustomRailSpec spec, CancellationToken ct)
    {
        var filters = HiddenContentService.Sanitize(RecommendationFilters.FromSpec(spec.Filters));
        var rows = await db.Series
            .AsNoTracking()
            .Select(s => new LibraryRailRow(
                s.Id, s.MangaBakaId, s.Title, s.SortTitle, s.Genres, s.Tags, s.ContentRating, s.Year,
                s.Status, s.Type, s.TotalChapters, s.Added))
            .ToListAsync(ct);

        // The index answers tags with their subtags and weights, and knows each title's score and
        // rank. It is only worth loading for a filter or sort that needs one of those; genres, years
        // and the rest are on the series already.
        var needsIndex = filters.NeedsTags || filters.MinRating is not null || spec.Sort == CustomRailSorts.Popular;
        var index = needsIndex && await store.IsAvailableAsync(ct) ? await vectorIndex.GetAsync(ct) : null;
        var plan = index?.Plan(filters);

        int? RowOf(LibraryRailRow r) =>
            index is not null && r.MangaBakaId is long id && index.TryGetRow(id, out var row) ? row : null;

        var matching = rows
            .Where(r => RowOf(r) is int row ? index!.Matches(row, plan!) : LibraryRailFilter.MatchesLocal(r, filters))
            .ToList();

        var lastRead = spec.Sort == CustomRailSorts.Read
            ? await LastReadAsync(ct)
            : new Dictionary<int, DateTime>();

        return LibraryRailFilter.Order(matching, spec.Sort, lastRead, r =>
            RowOf(r) is int row && index!.PopularityAt(row) is var rank && rank != VectorIndex.Unknown ? rank : null);
    }

    private async Task<Dictionary<int, DateTime>> LastReadAsync(CancellationToken ct)
    {
        var recent = await db.ChapterProgress
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Take(RecentProgressScan)
            .Select(p => new { p.SeriesId, p.UpdatedAt })
            .ToListAsync(ct);

        return recent
            .GroupBy(p => p.SeriesId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.UpdatedAt));
    }
}
