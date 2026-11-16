using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>
/// One custom rail's rows. Catalogue and recommendation rails fill <see cref="Items"/>; a library
/// rail fills <see cref="SeriesIds"/>, which the client renders from the series list it already has.
/// </summary>
/// <param name="Unavailable">
/// Set instead of rows when the source cannot answer: <c>catalogue</c> when the local MangaBaka
/// database is off or missing, <c>filterDeleted</c> when a library rail's saved Library filter is
/// gone. A code, worded by the client.
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
    MangaBakaLocalStore store,
    VectorIndexCache vectorIndex,
    IMemoryCache? cache = null,
    SourceAvailability? sourceAvailability = null,
    IAppSettings? appSettings = null,
    IUserSettings? userSettings = null,
    ILogger<CustomRailService>? logger = null)
{
    /// <summary>
    /// How long a user's projected library rows are reused. Home renders each library rail through
    /// its own request and the editor's live count re-evaluates on every edit, all over the same rows.
    /// </summary>
    private static readonly TimeSpan LibraryRowsFor = TimeSpan.FromSeconds(10);

    /// <summary>How many series the "read" sort looks back over, the same bound Home's reading rails use.</summary>
    private const int RecentProgressScan = 2000;

    public const string CatalogueUnavailable = "catalogue";

    public const string FilterDeleted = "filterDeleted";

    /// <summary>Saved Library filters are stored by the Library page; read them the way it writes them.</summary>
    private static readonly JsonSerializerOptions LibrarySpecJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<CustomRailItems> ItemsAsync(CustomRailSpec spec, int limit, CancellationToken ct = default)
    {
        spec = spec.Normalize();
        switch (spec.Source)
        {
            case CustomRailSources.Library when spec.LibraryFilterId is int filterId:
                return await SavedLibraryFilterAsync(filterId, ct) is { } saved
                    ? new CustomRailItems(spec.Source, [], saved.Take(limit).ToList())
                    : new CustomRailItems(spec.Source, [], [], FilterDeleted);

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
                    var exclude = await ExclusionsAsync(spec.ExcludeOwned, excludeInteracted: false, ct);
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
    /// tell: the catalogue sources need the search index for that, and Recommendations has no
    /// catalogue count at all - it draws from the ranked pool, not a filtered scan, so a count here
    /// would answer a question the source doesn't.
    /// </summary>
    public async Task<int?> CountAsync(CustomRailSpec spec, CancellationToken ct = default)
    {
        spec = spec.Normalize();
        if (spec.Source == CustomRailSources.Library)
        {
            return spec.LibraryFilterId is int filterId
                ? (await SavedLibraryFilterAsync(filterId, ct))?.Count
                : (await LibraryAsync(spec, ct)).Count;
        }

        if (spec.Source == CustomRailSources.Recommendations)
        {
            return null;
        }

        var scoped = await hidden.ScopeAsync(RecommendationFilters.FromSpec(spec.Filters), user.MaxContentRating, ct);
        var exclude = await ExclusionsAsync(spec.ExcludeOwned, excludeInteracted: false, ct);
        return await discover.CountAsync(new DiscoverFeedRequest(nameof(BrowseFeed.Popular), Filters: scoped), ct, exclude);
    }

    /// <summary>
    /// MangaBaka ids the caller should not be shown in a catalogue listing: whatever they hid or
    /// dismissed or marked as seen, plus their own series when <paramref name="excludeOwned"/> is set.
    /// <paramref name="excludeInteracted"/> adds both of those and every title they thumbed up or
    /// down. Folded into the scan rather than filtered off the page, so a rail is never cut short by it.
    /// </summary>
    public async Task<HashSet<long>> ExclusionsAsync(
        bool excludeOwned, bool excludeInteracted, CancellationToken ct = default)
    {
        var excluded = await RecommendationFeedbackService.SuppressedAsync(db, user.UserId, ct);
        if (excludeInteracted)
        {
            excluded.UnionWith(await db.RecommendationFeedback
                .AsNoTracking()
                .Where(x => x.UserId == user.UserId && x.Sentiment != RecommendationSentiment.None)
                .Select(x => x.ProviderId)
                .ToListAsync(ct));
        }

        if (excludeOwned || excludeInteracted)
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
        var rows = await LibraryRowsAsync(ct);

        // The index answers tags with their subtags and weights, and knows each title's score and
        // rank. It is only worth loading for a filter or sort that needs one of those; genres, years
        // and the rest are on the series already.
        var needsIndex = filters.NeedsTags || filters.MinRating is not null || spec.Sort == CustomRailSorts.Popular;
        var index = needsIndex && await store.IsAvailableAsync(ct) ? await vectorIndex.GetAsync(ct) : null;
        var plan = index?.Plan(filters);

        int? RowOf(LibraryRailRow r) =>
            index is not null && r.MangaBakaId is long id && index.TryGetRow(id, out var row) ? row : null;

        var matching = rows
            .Where(r => LibraryRailFilter.MatchesCredits(r, filters) &&
                (RowOf(r) is int row ? index!.Matches(row, plan!) : LibraryRailFilter.MatchesLocal(r, filters)))
            .ToList();

        var lastRead = spec.Sort == CustomRailSorts.Read
            ? await LastReadAsync(ct)
            : new Dictionary<int, DateTime>();

        return LibraryRailFilter.Order(matching, spec.Sort, lastRead, r =>
            RowOf(r) is int row && index!.PopularityAt(row) is var rank && rank != VectorIndex.Unknown ? rank : null);
    }

    /// <summary>
    /// The series a saved Library filter shows the caller, in its sort, evaluated over the Library
    /// list's own rows by <see cref="LibraryFilterEvaluator"/>. Null when the filter is gone, or is
    /// not the caller's: a rail can only use its owner's filters, whatever the query filter allows.
    /// </summary>
    private async Task<IReadOnlyList<int>?> SavedLibraryFilterAsync(int filterId, CancellationToken ct)
    {
        var stored = await db.SavedFilters
            .AsNoTracking()
            .Where(f => f.Id == filterId && f.UserId == user.UserId && f.Scope == SavedFilter.LibraryScope)
            .Select(f => f.Spec)
            .FirstOrDefaultAsync(ct);
        if (stored is null)
        {
            return null;
        }

        LibraryFilterSpec filter;
        try
        {
            filter = JsonSerializer.Deserialize<LibraryFilterSpec>(stored, LibrarySpecJson) ?? new LibraryFilterSpec();
        }
        catch (JsonException)
        {
            filter = new LibraryFilterSpec();
        }

        var (rows, readTracking) = await LibraryListAsync(ct);
        var context = new LibraryFilterContext(readTracking, user.MaxContentRating, DateTime.UtcNow);
        return LibraryFilterEvaluator.Apply(rows, filter, context).Select(s => s.Id).ToList();
    }

    /// <summary>
    /// The caller's Library list rows, the same ones <c>GET series</c> answers, plus whether anything
    /// tracks reading. Cached briefly like <see cref="LibraryRowsAsync"/>, since every rail on Home
    /// built from a saved filter reads the whole list.
    /// </summary>
    private static readonly KeyedAsyncLock<string> LibraryListBuilds = new();

    private async Task<(IReadOnlyList<SeriesDto> Rows, bool ReadTracking)> LibraryListAsync(CancellationToken ct)
    {
        var key = (Kind: "library-list-rows", user.UserId, user.AllRootFolders,
            Folders: string.Join(',', user.RootFolderIds.Order()));
        if (cache is not null && cache.TryGetValue(key, out (IReadOnlyList<SeriesDto>, bool) hit) && hit.Item1 is not null)
        {
            return hit;
        }

        // Several rails on Home load at once, and each would otherwise build the whole list.
        using var building = await LibraryListBuilds.AcquireAsync(key.ToString(), ct);
        if (cache is not null && cache.TryGetValue(key, out hit) && hit.Item1 is not null)
        {
            return hit;
        }

        if (sourceAvailability is null || appSettings is null)
        {
            throw new InvalidOperationException("Saved Library filter rails need SourceAvailability and IAppSettings.");
        }

        var titleLanguage = userSettings is null ? null : await userSettings.GetAsync(SettingKeys.UiTitleLanguage, ct);
        var rows = await LibraryListRows.BuildAsync(
            db, sourceAvailability, store, appSettings, titleLanguage,
            (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, null, ct);
        // The client's useReadTracking: the built-in reader has been used, or Kavita is connected.
        var readTracking = await db.ChapterProgress.AnyAsync(ct) ||
                           (!string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
                            !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaApiKey, ct)));
        (IReadOnlyList<SeriesDto>, bool) result = (rows, readTracking);
        cache?.Set(key, result, LibraryRowsFor);
        return result;
    }

    private async Task<IReadOnlyList<LibraryRailRow>> LibraryRowsAsync(CancellationToken ct)
    {
        var key = (Kind: "library-rail-rows", user.UserId, user.AllRootFolders,
            Folders: string.Join(',', user.RootFolderIds.Order()));
        if (cache is not null && cache.TryGetValue(key, out IReadOnlyList<LibraryRailRow>? hit) && hit is not null)
        {
            return hit;
        }

        var rows = await db.Series
            .AsNoTracking()
            .Active()
            .Select(s => new LibraryRailRow(
                s.Id, s.MangaBakaId, s.Title, s.SortTitle, s.Genres, s.Tags, s.ContentRating, s.Year,
                s.Status, s.Type, s.TotalChapters, s.Added, s.AuthorStory, s.AuthorArt, s.Publisher))
            .ToListAsync(ct);
        cache?.Set(key, (IReadOnlyList<LibraryRailRow>)rows, LibraryRowsFor);
        return rows;
    }

    private async Task<Dictionary<int, DateTime>> LastReadAsync(CancellationToken ct)
    {
        var recent = await db.ChapterProgress
            .AsNoTracking()
            .OwnedByScopeUser(db)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(RecentProgressScan)
            .Select(p => new { p.SeriesId, p.UpdatedAt })
            .ToListAsync(ct);

        return recent
            .GroupBy(p => p.SeriesId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.UpdatedAt));
    }
}
