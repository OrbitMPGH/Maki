using Mangarr.Data;
using Mangarr.Metadata.Embedding;
using Mangarr.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>One page of recommendations. <see cref="HasMore"/> means a deeper page exists in the cached pool.</summary>
public record RecommendationsResult(
    IReadOnlyList<MangaBakaRecommendation> Related,
    IReadOnlyList<MangaBakaRecommendation> Similar,
    DateTime GeneratedAt,
    int Page = 0,
    bool HasMore = false);

/// <summary>
/// Recommendation request. <see cref="SeedIds"/> are MangaBaka ids to base the picks on
/// (empty = the whole library); the rest constrain candidates. Any owned series is always
/// excluded from results, whether or not it's a seed. <see cref="Page"/> pages through the
/// cached similar pool ("Show more") without recomputing it.
/// </summary>
public record RecommendationRequest(
    IReadOnlyList<long>? SeedIds = null,
    RecommendationFilters? Filters = null,
    double Obscurity = 0,
    bool Refresh = false,
    int Page = 0);

/// <summary>
/// Library-based recommendations from the local MangaBaka dump: direct relations
/// (sequels/spin-offs/...) of library series plus a genre/tag/author similarity scan.
/// The scan reads the whole dump, so a pool of <see cref="PoolSize"/> similar picks is
/// computed once and cached until the library changes (or 12 h pass); requests then page
/// through it in <see cref="PageSize"/> slices. The UI's refresh button bypasses the cache.
/// </summary>
public class RecommendationService(
    IServiceScopeFactory scopeFactory,
    MangaBakaLocalStore store,
    SemanticRecommender semantic,
    ILogger<RecommendationService> logger)
{
    private const int PageSize = 40;
    private const int PoolSize = 200;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cacheKey;
    private RecommendationsResult? _cached;

    public async Task<RecommendationsResult> GetAsync(RecommendationRequest request, CancellationToken ct = default)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            throw new InvalidOperationException(
                "Recommendations need the local MangaBaka database (Settings → Metadata → local DB)");
        }

        List<long> libraryIds;
        // MangaBaka id -> rating weight (rating/5.0: 10→2.0, 5→1.0 neutral, 1→0.2). Only rated
        // series appear; unrated seeds default to weight 1.0 in the weighted mean.
        var ratingWeights = new Dictionary<long, double>();
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
            var rows = await db.Series
                .Where(s => s.MangaBakaId != null)
                .Select(s => new { Id = (long)s.MangaBakaId!.Value, s.Rating })
                .OrderBy(r => r.Id)
                .ToListAsync(ct);
            libraryIds = rows.Select(r => r.Id).ToList();
            foreach (var r in rows.Where(r => r.Rating is >= 1 and <= 10))
            {
                ratingWeights[r.Id] = r.Rating!.Value / 5.0;
            }
        }

        // Seeds default to the whole library. Owned series are always excluded from results.
        var filters = request.Filters ?? RecommendationFilters.None;
        var seeds = request.SeedIds is { Count: > 0 } chosen
            ? chosen.Distinct().OrderBy(id => id).ToList()
            : libraryIds;
        if (seeds.Count == 0)
        {
            return new RecommendationsResult([], [], DateTime.UtcNow);
        }

        // Only the weights of seeds actually in play affect this request; fold them into the key so
        // re-rating a seed recomputes the pool but re-rating an unrelated series doesn't.
        var weightKey = string.Join(",", seeds
            .Where(ratingWeights.ContainsKey)
            .Select(id => $"{id}:{ratingWeights[id]:F1}"));
        var key = $"{string.Join(",", seeds)}|lib:{string.Join(",", libraryIds)}|{FilterKey(filters)}|o:{request.Obscurity:F2}|w:{weightKey}";
        await _lock.WaitAsync(ct);
        try
        {
            var pool = !request.Refresh && _cached is not null && _cacheKey == key &&
                       DateTime.UtcNow - _cached.GeneratedAt < CacheFor
                ? _cached
                : null;

            if (pool is null)
            {
                var started = DateTime.UtcNow;
                var exclude = new HashSet<long>(libraryIds.Concat(seeds));
                var related = await store.GetRelatedAsync(seeds, exclude, ct);
                foreach (var r in related)
                {
                    exclude.Add(long.Parse(r.ProviderId));
                }

                // Prefer semantic ("feel") matches once the embedding index is built; fall back to
                // the genre/tag/author scan while it's still populating (or empty).
                var similar = semantic.IsReady()
                    ? await semantic.GetSimilarAsync(seeds, exclude, PoolSize, filters, request.Obscurity,
                        ratingWeights.Count > 0 ? ratingWeights : null, ct)
                    : [];
                var mode = similar.Count > 0 ? "semantic" : "genre";
                if (similar.Count == 0)
                {
                    similar = await store.GetSimilarAsync(seeds, exclude, PoolSize, filters, ct);
                }

                logger.LogInformation(
                    "Computed recommendations for {SeedCount} seed(s) in {Elapsed:F1}s: {Related} related, {Similar} similar ({Mode})",
                    seeds.Count, (DateTime.UtcNow - started).TotalSeconds, related.Count, similar.Count, mode);

                _cacheKey = key;
                _cached = pool = new RecommendationsResult(related, similar, DateTime.UtcNow);
            }

            var page = Math.Max(0, request.Page);
            return pool with
            {
                Similar = pool.Similar.Skip(page * PageSize).Take(PageSize).ToList(),
                Page = page,
                HasMore = pool.Similar.Count > (page + 1) * PageSize,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string FilterKey(RecommendationFilters f) =>
        $"{f.YearMin}-{f.YearMax}-{f.MinRating}-{string.Join('.', f.Types ?? [])}-{string.Join('.', f.Statuses ?? [])}" +
        $"-{string.Join('.', f.Genres ?? [])}-{f.MinChapters}-{f.MaxChapters}-{string.Join('.', f.Tags ?? [])}";
}
