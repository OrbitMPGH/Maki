using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>One catalogue-browse rail for the Discover page.</summary>
public record DiscoverRail(string Key, string Title, IReadOnlyList<MangaBakaRecommendation> Items);

/// <summary>
/// Builds the Discover page's catalogue-browse rails from the local MangaBaka dump: the main
/// browse set (Popular / New / Trending / Top rated / per-type) and a per-genre set (one
/// "Popular in {genre}" rail per genre). Each rail is a full-table scan, so each set is computed
/// once and cached for <see cref="CacheFor"/>; the rails don't depend on the user's library, so
/// the caches are global and only the UI's refresh button busts them. Mirrors the caching shape
/// of <see cref="RecommendationService"/>.
/// </summary>
public class DiscoverService(MangaBakaLocalStore store, ILogger<DiscoverService> logger)
{
    private const int RailSize = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

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
            var rails = new List<DiscoverRail>(Rails.Length);
            foreach (var (feed, key, title) in Rails)
            {
                var items = await store.GetBrowseAsync(feed, RailSize, ct: ct);
                if (items.Count > 0)
                {
                    rails.Add(new DiscoverRail(key, title, items));
                }
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
                    var items = await store.GetBrowseAsync(BrowseFeed.GenreSpotlight, RailSize, genre, ct);
                    return items.Count > 0
                        ? new DiscoverRail($"genre-{genre.ToLowerInvariant().Replace(' ', '-')}", $"Popular in {genre}", items)
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

    private async Task EnsureAvailableAsync(CancellationToken ct)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            throw new InvalidOperationException(
                "Discover needs the local MangaBaka database (Settings → Metadata → local DB)");
        }
    }
}
