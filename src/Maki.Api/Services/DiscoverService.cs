using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>One catalogue-browse rail for the Discover page.</summary>
public record DiscoverRail(string Key, string Title, IReadOnlyList<MangaBakaRecommendation> Items);

/// <summary>
/// Builds the Discover page's catalogue-browse rails (Popular / New / Trending / Top rated /
/// per-type) from the local MangaBaka dump. Each rail is a full-table scan, so the whole set is
/// computed once and cached for <see cref="CacheFor"/>; the rails don't depend on the user's
/// library, so the cache is global and only the UI's refresh button busts it. Mirrors the
/// caching shape of <see cref="RecommendationService"/>.
/// </summary>
public class DiscoverService(MangaBakaLocalStore store, ILogger<DiscoverService> logger)
{
    private const int RailSize = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    // Order here is the order rails render on the page. The genre spotlight is spliced in below.
    private static readonly (BrowseFeed Feed, string Key, string Title)[] Rails =
    [
        (BrowseFeed.Trending, "trending", "Trending now"),
        (BrowseFeed.Popular, "popular", "Most popular"),
        (BrowseFeed.New, "new", "Newly released"),
        (BrowseFeed.TopRated, "top-rated", "Top rated"),
        (BrowseFeed.PopularManhwa, "popular-manhwa", "Popular manhwa"),
        (BrowseFeed.PopularManhua, "popular-manhua", "Popular manhua"),
    ];

    // Rendered position of the genre spotlight (after "Newly released").
    private const int SpotlightIndex = 3;

    // Genres from the MangaBaka vocabulary that reliably fill a popularity-ranked rail. One is
    // spotlighted per day (rotating), so the tab looks different on repeat visits.
    private static readonly string[] SpotlightGenres =
    [
        "Action", "Adventure", "Comedy", "Drama", "Fantasy", "Horror", "Mystery", "Romance",
        "Sci-Fi", "Slice of Life", "Sports", "Supernatural", "Thriller", "Psychological",
        "Historical", "Martial Arts", "School Life", "Boys Love", "Girls Love",
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<DiscoverRail>? _cached;
    private DateTime _generatedAt;

    public async Task<IReadOnlyList<DiscoverRail>> GetFeedsAsync(bool refresh, CancellationToken ct = default)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            throw new InvalidOperationException(
                "Discover needs the local MangaBaka database (Settings → Metadata → local DB)");
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh && _cached is not null && DateTime.UtcNow - _generatedAt < CacheFor)
            {
                return _cached;
            }

            var started = DateTime.UtcNow;
            var rails = new List<DiscoverRail>(Rails.Length + 1);
            foreach (var (feed, key, title) in Rails)
            {
                var items = await store.GetBrowseAsync(feed, RailSize, ct: ct);
                if (items.Count > 0)
                {
                    rails.Add(new DiscoverRail(key, title, items));
                }
            }

            // Daily-rotating genre spotlight, spliced into the order (after "Newly released").
            var genre = SpotlightGenres[DateTime.UtcNow.DayOfYear % SpotlightGenres.Length];
            var spotlight = await store.GetBrowseAsync(BrowseFeed.GenreSpotlight, RailSize, genre, ct);
            if (spotlight.Count > 0)
            {
                var at = Math.Min(SpotlightIndex, rails.Count);
                rails.Insert(at, new DiscoverRail("genre-spotlight", $"Popular in {genre}", spotlight));
            }

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
}
