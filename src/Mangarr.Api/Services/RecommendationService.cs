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

    public async Task<RecommendationsResult> GetAsync(bool refresh, CancellationToken ct = default)
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

        var key = string.Join(",", libraryIds);
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh && _cached is not null && _cacheKey == key &&
                DateTime.UtcNow - _cached.GeneratedAt < CacheFor)
            {
                return _cached;
            }

            var started = DateTime.UtcNow;
            var related = await store.GetRelatedAsync(libraryIds, ct);
            var relatedIds = related.Select(r => long.Parse(r.ProviderId)).ToList();

            // Prefer semantic ("feel") matches once the embedding index is built; fall back to
            // the genre/tag/author scan while it's still populating (or empty).
            var similar = semantic.IsReady()
                ? await semantic.GetSimilarAsync(libraryIds, relatedIds, SimilarLimit, ct)
                : [];
            var mode = similar.Count > 0 ? "semantic" : "genre";
            if (similar.Count == 0)
            {
                similar = await store.GetSimilarAsync(libraryIds, relatedIds, SimilarLimit, ct);
            }

            logger.LogInformation(
                "Computed recommendations for {LibraryCount} series in {Elapsed:F1}s: {Related} related, {Similar} similar ({Mode})",
                libraryIds.Count, (DateTime.UtcNow - started).TotalSeconds, related.Count, similar.Count, mode);

            _cacheKey = key;
            _cached = new RecommendationsResult(related, similar, DateTime.UtcNow);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
