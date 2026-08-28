using Maki.Core.Metadata;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>
/// One catalogue-browse rail for the Discover page. <see cref="Feed"/> (a <see cref="BrowseFeed"/>
/// name) and <see cref="Genre"/> identify the rail's source so the "Show more" view can re-query it
/// with filters and a higher limit.
/// </summary>
public record DiscoverRail(
    string Key, string Title, string Feed, string? Genre, IReadOnlyList<MangaBakaRecommendation> Items);

/// <summary>How a browse page is ordered when it is resolved in memory.</summary>
public static class BrowseSort
{
    public const string Popular = "popular";
    public const string Rating = "rating";
    public const string Newest = "newest";
    public const string Oldest = "oldest";
}

/// <summary>Request for the expanded (filtered, larger, pageable) view of a single rail.</summary>
/// <param name="Offset">
/// Rows to skip. Honoured only on the in-memory path, which is the only one that can page
/// coherently: <see cref="MangaBakaLocalStore.GetBrowseAsync"/> over-fetches and dedupes by title in
/// C#, so it has no stable notion of "the next page".
/// </param>
public record DiscoverFeedRequest(
    string Feed,
    string? Genre = null,
    RecommendationFilters? Filters = null,
    int Limit = 120,
    int Offset = 0,
    string Sort = BrowseSort.Popular);

/// <summary>One creator or publisher, and the works credited to them.</summary>
public record CreatorRequest(
    string Name,
    string? Role = null,
    RecommendationFilters? Filters = null,
    string Sort = BrowseSort.Popular,
    int Offset = 0,
    int Limit = 60);

/// <param name="WorkCount">Everything credited to them, before filters and paging.</param>
public record CreatorProfile(
    string Name,
    IReadOnlyList<string> Roles,
    int WorkCount,
    IReadOnlyList<MangaBakaRecommendation> Items);

/// <summary>Free-text Discover search — a plot description, a mood, or just a title.</summary>
/// <param name="Engine">
/// Which engine to ask: <c>auto</c> (semantic, falling back to the title index, and what every
/// caller got before this existed), <c>semantic</c>, or <c>title</c>. Deliberately not called
/// <c>Mode</c>: <see cref="DiscoverSearchResponse.Mode"/> reports which engine <em>answered</em>,
/// while this says which one was <em>asked for</em>, and conflating the two hides the fallback.
/// </param>
public record DiscoverSearchRequest(
    string Query,
    RecommendationFilters? Filters = null,
    int Limit = 60,
    string Engine = DiscoverSearchRequest.AutoEngine)
{
    public const string AutoEngine = "auto";
    public const string SemanticEngine = "semantic";
    public const string TitleEngine = "title";

    /// <summary>True when the caller explicitly asked for plain title matching.</summary>
    public bool WantsTitleOnly =>
        string.Equals(Engine, TitleEngine, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Search results plus which engine answered: <c>semantic</c> when the embedding index served it,
/// <c>title</c> when it fell back to the FTS5 title index (index not built yet). The UI says so,
/// because the two behave very differently on a descriptive query.
/// </summary>
/// <param name="CorrectedQuery">
/// The spelling that actually found something, when the query as typed found next to nothing. The
/// UI shows it as "showing results for ..."; null means no correction was needed.
/// </param>
/// <param name="Credits">Creators the query named or was recognised as naming, for display as chips.</param>
public record DiscoverSearchResponse(
    string Mode,
    IReadOnlyList<MangaBakaRecommendation> Items,
    string? CorrectedQuery = null,
    IReadOnlyList<ResolvedCredit>? Credits = null);

/// <summary>
/// Builds the Discover page's catalogue-browse rails from the local MangaBaka dump: the main
/// browse set (Popular / New / Trending / Top rated / per-type) and a per-genre set (one
/// "Popular in {genre}" rail per genre). Each rail is a full-table scan, so each set is computed
/// once and cached for <see cref="CacheFor"/>; the rails don't depend on the user's library, so
/// the caches are global and only the UI's refresh button busts them. Mirrors the caching shape
/// of <see cref="RecommendationService"/>.
/// </summary>
public class DiscoverService(
    MangaBakaLocalStore store,
    SemanticSearcher searcher,
    VectorIndexCache vectorIndex,
    CatalogueIndexCache catalogueIndex,
    ILogger<DiscoverService> logger)
{
    private const int RailSize = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    // The catalogue-browse rails are cached once for the whole instance with no viewer in scope
    // (see the class doc), so there's no per-user ceiling to resolve here — fall back to the same
    // floor a freshly-provisioned account gets (see ContentRating.Default) rather than showing
    // everyone whatever the least-restricted account on the instance could see.
    private static readonly RecommendationFilters SafeDefaultFilters =
        new(ContentRatings: ContentRating.Allowed(ContentRating.Default));

    // Order here is the order rails render on the browse tab.
    private static readonly (BrowseFeed Feed, string Key, string Title)[] Rails =
    [
        (BrowseFeed.Trending, "trending", "Trending now"),
        (BrowseFeed.Popular, "popular", "Most popular"),
        (BrowseFeed.New, "new", "Newly released"),
        (BrowseFeed.TopRated, "top-rated", "Top rated"),
        (BrowseFeed.PopularManhwa, "popular-manhwa", "Popular manhwa"),
        (BrowseFeed.PopularManhua, "popular-manhua", "Popular manhua"),
    ];

    // Genres from the MangaBaka vocabulary that reliably fill a popularity-ranked rail. Each gets
    // its own rail on the Genres tab, in this order.
    private static readonly string[] Genres =
    [
        "Action", "Adventure", "Fantasy", "Romance", "Comedy", "Drama", "Slice of Life",
        "Supernatural", "Mystery", "Horror", "Sci-Fi", "Thriller", "Psychological", "Sports",
        "Martial Arts", "Historical", "School Life", "Boys Love", "Girls Love",
    ];

    // Bounds concurrent full-table scans for the per-genre set (own connection each; readonly).
    private static readonly int GenreScanConcurrency = Math.Min(6, Environment.ProcessorCount);

    // Same for the six browse rails. Capped at the rail count, so on a small box this degrades to
    // the old serial behaviour rather than oversubscribing a disk that is already the bottleneck.
    private static readonly int FeedScanConcurrency = Math.Min(Rails.Length, Environment.ProcessorCount);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<DiscoverRail>? _cached;
    private DateTime _generatedAt;

    private readonly SemaphoreSlim _genreLock = new(1, 1);
    private IReadOnlyList<DiscoverRail>? _cachedGenres;
    private DateTime _genresGeneratedAt;

    public async Task<IReadOnlyList<DiscoverRail>> GetFeedsAsync(bool refresh, CancellationToken ct = default)
    {
        await EnsureAvailableAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh && _cached is not null && DateTime.UtcNow - _generatedAt < CacheFor)
            {
                return _cached;
            }

            var started = DateTime.UtcNow;
            // Bounded-concurrent, same as the genre set below: each rail is an independent query on
            // its own connection. These ran serially until the browse indexes landed, which made a
            // cold cache cost the sum of six full scans rather than the slowest one.
            using var gate = new SemaphoreSlim(FeedScanConcurrency);
            var tasks = Rails.Select(async rail =>
            {
                var (feed, key, title) = rail;
                await gate.WaitAsync(ct);
                try
                {
                    var items = await store.GetBrowseAsync(
                        feed, RailSize, filters: SafeDefaultFilters, ct: ct);
                    return items.Count > 0
                        ? new DiscoverRail(key, title, feed.ToString(), null, items)
                        : null;
                }
                finally
                {
                    gate.Release();
                }
            });

            // Preserve the declared rail order (WhenAll keeps input order).
            var rails = (await Task.WhenAll(tasks)).Where(r => r is not null).Cast<DiscoverRail>().ToList();

            logger.LogInformation(
                "Computed {Count} Discover rail(s) in {Elapsed:F1}s",
                rails.Count, (DateTime.UtcNow - started).TotalSeconds);

            _cached = rails;
            _generatedAt = DateTime.UtcNow;
            return rails;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>One "Popular in {genre}" rail per genre, for the Genres tab.</summary>
    public async Task<IReadOnlyList<DiscoverRail>> GetGenreFeedsAsync(bool refresh, CancellationToken ct = default)
    {
        await EnsureAvailableAsync(ct);
        await _genreLock.WaitAsync(ct);
        try
        {
            if (!refresh && _cachedGenres is not null && DateTime.UtcNow - _genresGeneratedAt < CacheFor)
            {
                return _cachedGenres;
            }

            var started = DateTime.UtcNow;
            // Scan genres concurrently (bounded) — each is an independent full-table scan.
            using var gate = new SemaphoreSlim(GenreScanConcurrency);
            var tasks = Genres.Select(async genre =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var items = await store.GetBrowseAsync(
                        BrowseFeed.GenreSpotlight, RailSize, genre, SafeDefaultFilters, ct);
                    return items.Count > 0
                        ? new DiscoverRail(
                            $"genre-{genre.ToLowerInvariant().Replace(' ', '-')}", $"Popular in {genre}",
                            BrowseFeed.GenreSpotlight.ToString(), genre, items)
                        : null;
                }
                finally
                {
                    gate.Release();
                }
            });

            // Preserve the declared genre order (WhenAll keeps input order).
            var rails = (await Task.WhenAll(tasks)).Where(r => r is not null).Cast<DiscoverRail>().ToList();

            logger.LogInformation(
                "Computed {Count} Discover genre rail(s) in {Elapsed:F1}s",
                rails.Count, (DateTime.UtcNow - started).TotalSeconds);

            _cachedGenres = rails;
            _genresGeneratedAt = DateTime.UtcNow;
            return rails;
        }
        finally
        {
            _genreLock.Release();
        }
    }

    /// <summary>
    /// The expanded view of one rail: the same ordering, with the user's filters applied, pageable,
    /// and a higher limit. Not cached — it's a user-initiated, parameterised query.
    ///
    /// <para>
    /// Resolved against the in-memory <see cref="VectorIndex"/> whenever that index can answer the
    /// request, and against the dump otherwise. This is not only about speed. A tag filter cannot be
    /// expressed in the SQL path at all: <c>RecommendationFilters.BuildClause</c> emits year, rating,
    /// chapters, genres, types, statuses and content ratings, and silently emits nothing for tags,
    /// so asking the dump for "isekai" returns everything. The vector index tests tags per row
    /// against the packed blobs it already carries, alongside every other filter, which is also what
    /// makes them apply before the page is cut rather than after.
    /// </para>
    ///
    /// <para>
    /// Trending and New keep the SQL ordering when no tag filter is involved, because they rank on
    /// popularity history and publication date, neither of which the index carries. Ask for a tag
    /// alongside them and the in-memory path takes over with the nearest ordering it has, since a
    /// filter that is quietly ignored is worse than one that is approximately ordered.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetFeedAsync(
        DiscoverFeedRequest request, CancellationToken ct = default)
    {
        await EnsureAvailableAsync(ct);

        if (!Enum.TryParse<BrowseFeed>(request.Feed, ignoreCase: true, out var feed))
        {
            throw new InvalidOperationException($"Unknown feed '{request.Feed}'.");
        }

        // 600 rather than 300: the in-memory path already scans and sorts the whole index whatever
        // the page size is, so the only cost of a deeper page is the hydration query, and browsing a
        // filtered catalogue is exactly the case where people keep pressing Load more.
        var limit = Math.Clamp(request.Limit, 1, 600);
        var offset = Math.Max(0, request.Offset);
        var wantsTags = request.Filters?.Tags is { Count: > 0 };

        if (OrderableInIndex(feed) || wantsTags || offset > 0)
        {
            if (await vectorIndex.GetAsync(ct) is { } index)
            {
                var ids = SelectRows(index, feed, request, offset, limit);
                return await store.GetByIdsAsync(ids, request.Filters?.ContentRatings, ct);
            }

            if (wantsTags)
            {
                logger.LogDebug("Tag filter dropped: the search index is not built, and the dump cannot express it");
            }
        }

        return await store.GetBrowseAsync(feed, limit, request.Genre, request.Filters, ct);
    }

    /// <summary>One creator or publisher and their works, for the creator page.</summary>
    public async Task<CreatorProfile?> GetCreatorAsync(CreatorRequest request, CancellationToken ct = default)
    {
        await EnsureAvailableAsync(ct);

        if (await catalogueIndex.GetAsync(ct) is not { } catalogue || catalogue.Credits.IsEmpty)
        {
            return null;
        }

        var role = CreditIndex.ParseRole(request.Role);
        if (!catalogue.Credits.TryResolveFuzzy(request.Name, role, maxDistance: 1, out var nameId))
        {
            return null;
        }

        var works = catalogue.Credits.WorksOf(nameId, role);
        // 600 rather than 300: the in-memory path already scans and sorts the whole index whatever
        // the page size is, so the only cost of a deeper page is the hydration query, and browsing a
        // filtered catalogue is exactly the case where people keep pressing Load more.
        var limit = Math.Clamp(request.Limit, 1, 600);
        var offset = Math.Max(0, request.Offset);

        // Ordering and the structured filters both need the index; without it the works still list,
        // in the popularity order CreditIndex stores them in. The content-rating ceiling is the one
        // filter that must not degrade with it — it is what the caller is allowed to see, not how
        // they asked to narrow it — so it is applied during hydration on both paths instead.
        IReadOnlyList<long> page;
        if (await vectorIndex.GetAsync(ct) is { } index)
        {
            var plan = index.Plan(request.Filters) with { CreditMask = index.BuildRowMask(works) };
            page = OrderRows(index, plan, request.Sort, offset, limit);
        }
        else
        {
            page = works.Skip(offset).Take(limit).ToList();
        }

        return new CreatorProfile(
            catalogue.Credits.NameAt(nameId),
            catalogue.Credits.RoleLabelsAt(nameId),
            works.Length,
            await store.GetByIdsAsync(page, request.Filters?.ContentRatings, ct));
    }

    /// <summary>Name suggestions for a partly typed creator or publisher.</summary>
    public async Task<IReadOnlyList<ResolvedCredit>> SuggestCreditsAsync(
        string query, string? role, int limit, CancellationToken ct = default)
    {
        if (await catalogueIndex.GetAsync(ct) is not { } catalogue)
        {
            return [];
        }

        var wanted = CreditIndex.ParseRole(role);
        return catalogue.Credits
            .Suggest(query, wanted, Math.Clamp(limit, 1, 50))
            .Select(m => new ResolvedCredit(
                catalogue.Credits.NameAt(m.NameId),
                catalogue.Credits.RoleLabelsAt(m.NameId),
                m.WorkCount))
            .ToList();
    }

    /// <summary>Feeds whose ordering the vector index can reproduce exactly.</summary>
    private static bool OrderableInIndex(BrowseFeed feed) => feed is
        BrowseFeed.Popular or BrowseFeed.TopRated or
        BrowseFeed.PopularManhwa or BrowseFeed.PopularManhua or BrowseFeed.GenreSpotlight;

    /// <summary>Turns a feed plus the caller's filters into one page of series ids.</summary>
    private static IReadOnlyList<long> SelectRows(
        VectorIndex index, BrowseFeed feed, DiscoverFeedRequest request, int offset, int limit)
    {
        var filters = request.Filters ?? RecommendationFilters.None;

        if (feed == BrowseFeed.GenreSpotlight && !string.IsNullOrWhiteSpace(request.Genre))
        {
            filters = filters with { Genres = [.. filters.Genres ?? [], request.Genre] };
        }

        var railType = feed switch
        {
            BrowseFeed.PopularManhwa => "manhwa",
            BrowseFeed.PopularManhua => "manhua",
            _ => null,
        };

        if (railType is not null)
        {
            // Intersect rather than overwrite: the rail's own type is part of what was asked for,
            // and a user narrowing it further must not widen it back out.
            filters = filters with
            {
                Types = filters.Types is { Count: > 0 } chosen
                    ? chosen.Where(t => string.Equals(t, railType, StringComparison.OrdinalIgnoreCase)).ToList()
                    : [railType],
            };

            if (filters.Types.Count == 0)
            {
                return [];
            }
        }

        var sort = request.Sort;
        if (feed == BrowseFeed.TopRated)
        {
            sort = BrowseSort.Rating;
        }
        else if (feed == BrowseFeed.New)
        {
            sort = BrowseSort.Newest;
        }

        return OrderRows(index, index.Plan(filters), sort, offset, limit);
    }

    /// <summary>Every row a plan allows, ordered, then paged.</summary>
    private static IReadOnlyList<long> OrderRows(
        VectorIndex index, FilterPlan plan, string sort, int offset, int limit)
    {
        if (plan.Impossible)
        {
            return [];
        }

        var rows = new List<int>(Math.Min(index.Count, 8192));
        for (var row = 0; row < index.Count; row++)
        {
            if (index.Matches(row, plan))
            {
                rows.Add(row);
            }
        }

        // Unknown is stored as -1, which a plain ascending compare would rank as the most popular
        // series in the catalogue and the oldest title in it.
        int Rank(int row) => index.PopularityAt(row) == VectorIndex.Unknown
            ? int.MaxValue
            : index.PopularityAt(row);
        int Year(int row) => index.YearAt(row);

        Comparison<int> order = sort switch
        {
            BrowseSort.Rating => (a, b) =>
            {
                var byRating = index.RatingAt(b).CompareTo(index.RatingAt(a));
                return byRating != 0 ? byRating : Rank(a).CompareTo(Rank(b));
            },
            BrowseSort.Newest => (a, b) =>
            {
                var byYear = Year(b).CompareTo(Year(a));
                return byYear != 0 ? byYear : Rank(a).CompareTo(Rank(b));
            },
            BrowseSort.Oldest => (a, b) =>
            {
                var yearA = Year(a) == VectorIndex.Unknown ? int.MaxValue : Year(a);
                var yearB = Year(b) == VectorIndex.Unknown ? int.MaxValue : Year(b);
                var byYear = yearA.CompareTo(yearB);
                return byYear != 0 ? byYear : Rank(a).CompareTo(Rank(b));
            },
            _ => (a, b) =>
            {
                var byPopularity = Rank(a).CompareTo(Rank(b));
                return byPopularity != 0 ? byPopularity : index.RatingAt(b).CompareTo(index.RatingAt(a));
            },
        };

        rows.Sort(order);
        return rows.Skip(offset).Take(limit).Select(index.IdAt).ToList();
    }

    /// <summary>
    /// Free-text search over the catalogue. Prefers the semantic engine (query embedding fused
    /// with the title index); falls back to plain title search when the embedding index hasn't
    /// been built, so the box is never dead — the response says which one answered.
    /// </summary>
    public async Task<DiscoverSearchResponse> SearchAsync(
        DiscoverSearchRequest request, CancellationToken ct = default)
    {
        await EnsureAvailableAsync(ct);

        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return new DiscoverSearchResponse("semantic", []);
        }

        if (!request.WantsTitleOnly && searcher.IsReady())
        {
            var outcome = await searcher.SearchAsync(query, request.Filters, request.Limit, ct);
            if (outcome.Items.Count > 0)
            {
                return new DiscoverSearchResponse(
                    "semantic", outcome.Items, outcome.CorrectedQuery, outcome.Credits);
            }

            // A resolved credit that matched nothing is a real answer ("no such author", or nobody
            // whose work fits the filters), not a reason to go looking for title hits that would
            // ignore the credit entirely.
            if (request.Filters is not null || outcome.Credits.Count > 0)
            {
                return new DiscoverSearchResponse("semantic", [], null, outcome.Credits);
            }
        }

        logger.LogDebug("Answering a Discover query from the title index");
        // store.SearchAsync takes a single ceiling rather than a list; recover it from the already
        // ceiling-resolved Filters.ContentRatings (Allowed/Clamp always produce a prefix of
        // ContentRating.All, so its highest member is the ceiling) so this fallback stays in step
        // with the semantic path it stands in for instead of using a different rule.
        var maxAllowed = request.Filters?.ContentRatings is { Count: > 0 } allowedRatings
            ? ContentRating.All.LastOrDefault(allowedRatings.Contains) ?? ContentRating.Default
            : ContentRating.Default;
        var titleHits = await store.SearchWithCorrectionAsync(query, maxAllowed, limit: request.Limit, ct: ct);
        return new DiscoverSearchResponse(
            "title",
            titleHits.Items.Select(ToRecommendation).ToList(),
            titleHits.CorrectedQuery,
            titleHits.Credits);
    }

    /// <summary>Shapes a title-index hit like a semantic one so the UI renders one card type.</summary>
    private static MangaBakaRecommendation ToRecommendation(MetadataSearchResult hit) =>
        new(hit.ProviderId, hit.Title, hit.CoverUrl, hit.Year, hit.Description,
            hit.Status, null, hit.TotalChapters, [], [], false, null, null);

    private async Task EnsureAvailableAsync(CancellationToken ct)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            throw new InvalidOperationException(
                "Discover needs the local MangaBaka database (Settings → Metadata → local DB)");
        }
    }
}
