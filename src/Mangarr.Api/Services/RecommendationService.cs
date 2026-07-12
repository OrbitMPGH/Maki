using Mangarr.Data;
using Mangarr.Metadata.Embedding;
using Mangarr.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

public record RecommendationsResult(
    IReadOnlyList<MangaBakaRecommendation> Related,
    IReadOnlyList<MangaBakaRecommendation> Similar,
    DateTime GeneratedAt);

/// <summary>
/// Recommendation request. <see cref="SeedIds"/> are MangaBaka ids to base the picks on
/// (empty = the whole library); the rest constrain candidates. Any owned series is always
/// excluded from results, whether or not it's a seed.
/// </summary>
public record RecommendationRequest(
    IReadOnlyList<long>? SeedIds = null,
    RecommendationFilters? Filters = null,
    double Obscurity = 0,
    bool Refresh = false);

/// <summary>
/// Library-based recommendations from the local MangaBaka dump: direct relations
/// (sequels/spin-offs/...) of library series plus a genre/tag/author similarity scan.
/// The scan reads the whole dump, so results are cached until the library changes
/// (or 12 h pass); the UI's refresh button bypasses the cache.
/// </summary>
public class RecommendationService(
    IServiceScopeFactory scopeFactory,
    MangaBakaLocalStore store,
    SemanticRecommender semantic,
    ILogger<RecommendationService> logger)
{
    private const int SimilarLimit = 40;
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
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
            libraryIds = await db.Series
                .Where(s => s.MangaBakaId != null)
                .Select(s => (long)s.MangaBakaId!.Value)
                .OrderBy(id => id)
                .ToListAsync(ct);
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

        var key = $"{string.Join(",", seeds)}|lib:{string.Join(",", libraryIds)}|{FilterKey(filters)}|o:{request.Obscurity:F2}";
        await _lock.WaitAsync(ct);
        try
        {
            if (!request.Refresh && _cached is not null && _cacheKey == key &&
                DateTime.UtcNow - _cached.GeneratedAt < CacheFor)
            {
                return _cached;
            }

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
                ? await semantic.GetSimilarAsync(seeds, exclude, SimilarLimit, filters, request.Obscurity, ct)
                : [];
            var mode = similar.Count > 0 ? "semantic" : "genre";
            if (similar.Count == 0)
            {
                similar = await store.GetSimilarAsync(seeds, exclude, SimilarLimit, filters, ct);
            }

            logger.LogInformation(
                "Computed recommendations for {SeedCount} seed(s) in {Elapsed:F1}s: {Related} related, {Similar} similar ({Mode})",
                seeds.Count, (DateTime.UtcNow - started).TotalSeconds, related.Count, similar.Count, mode);

            _cacheKey = key;
            _cached = new RecommendationsResult(related, similar, DateTime.UtcNow);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string FilterKey(RecommendationFilters f) =>
        $"{f.YearMin}-{f.YearMax}-{f.MinRating}-{string.Join('.', f.Types ?? [])}-{string.Join('.', f.Statuses ?? [])}";
}
