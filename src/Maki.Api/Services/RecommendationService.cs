using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

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
/// <param name="Diversity">
/// 0 (closest matches first, the default) … 1 (spread the picks out). Feeds the MMR re-rank, so it
/// changes the order and membership of the pool, not the filters — which is why it is part of the
/// cache key rather than something the pager can apply per page.
/// </param>
public record RecommendationRequest(
    IReadOnlyList<long>? SeedIds = null,
    RecommendationFilters? Filters = null,
    double Obscurity = 0,
    bool Refresh = false,
    int Page = 0,
    double Diversity = 0);

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
    SeedWeightService seedWeights,
    IAppSettings settings,
    ILogger<RecommendationService> logger)
{
    private const int PageSize = 40;
    private const int PoolSize = 200;

    /// <summary>
    /// How many distinct pools to keep. More than one because the cache key carries the caller's
    /// library, ratings and derived taste weights, so on a multi-user instance every person has their
    /// own key — a single slot would thrash between them and recompute a full index scan per request.
    /// Two per person, in fact: the Recommended tab seeds on the whole library while Discover's
    /// recent-activity rail seeds on the last few series read, and the rail is on the tab people land
    /// on. Small even so, because a pool is 200 hydrated recommendations and stale ones age out on
    /// their own.
    /// </summary>
    private const int CacheSlots = 16;

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, RecommendationsResult> _pools = [];

    /// <param name="scope">
    /// The caller's data scope, applied to the child scope this opens. A singleton creating its own
    /// scope gets a fresh unrestricted <see cref="DataScope"/>, which would seed recommendations from
    /// root folders the caller was never granted and weight them with somebody else's ratings.
    /// </param>
    public async Task<RecommendationsResult> GetAsync(
        RecommendationRequest request, ICurrentUser scope, CancellationToken ct = default)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            throw new InvalidOperationException(
                "Recommendations need the local MangaBaka database (Settings → Metadata → local DB)");
        }

        // MangaBaka id -> seed weight. A rated series gets rating/5.0 (10→2.0, 5→1.0 neutral, 1→0.2);
        // an unrated one gets whatever its reading history implies, or nothing at all if there is no
        // history to read. Seeds absent from here default to 1.0 in the weighted mean.
        SeedWeights seeded;
        using (var dbScope = scopeFactory.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.Scope.SetUser(scope.UserId, scope.AllRootFolders);
            seeded = await seedWeights.BuildAsync(db, scope, ct);
        }

        var libraryIds = seeded.LibraryIds;
        var seedWeight = seeded.Weights;

        // Seeds default to the whole library. Owned series are always excluded from results.
        var filters = request.Filters ?? RecommendationFilters.None;
        filters = filters with
        {
            ContentRatings = filters.ContentRatings is { Count: > 0 } requested
                ? ContentRating.Clamp(requested, scope.MaxContentRating)
                : ContentRating.Allowed(scope.MaxContentRating)
        };
        IReadOnlyList<long> seeds = request.SeedIds is { Count: > 0 } chosen
            ? chosen.Distinct().OrderBy(id => id).ToList()
            : libraryIds;
        if (seeds.Count == 0)
        {
            return new RecommendationsResult([], [], DateTime.UtcNow);
        }

        // Only the weights of seeds actually in play affect this request; fold them into the key so
        // re-rating a seed recomputes the pool but re-rating an unrelated series doesn't. The F1 here
        // is the same resolution TasteTuning.WeightQuantum rounds a derived weight to, so a chapter
        // read does not silently invalidate a 12-hour pool — see that constant's remarks.
        var weightKey = string.Join(",", seeds
            .Where(seedWeight.ContainsKey)
            .Select(id => $"{id}:{seedWeight[id]:F1}"));
        // The co-recommendation switch belongs in the key for the same reason everything else here
        // does: flipping it changes the pool, and without it the change would sit invisible behind a
        // 12-hour hit until the entry aged out.
        var coGraph = await CoGraphEnabledAsync(ct);
        var coRead = await CoReadEnabledAsync(ct);
        // Named for the artifact, not "taste": SeedWeightService's behavioural channel weights
        // SEEDS and is a different feature entirely.
        var tasteVectors = await TasteEnabledAsync(ct);
        var key = $"{string.Join(",", seeds)}|lib:{string.Join(",", libraryIds)}|{FilterKey(filters)}" +
                  $"|o:{request.Obscurity:F2}|d:{request.Diversity:F2}|w:{weightKey}" +
                  $"|g:{(coGraph ? 1 : 0)}|c:{(coRead ? 1 : 0)}|t:{(tasteVectors ? 1 : 0)}";
        await _lock.WaitAsync(ct);
        try
        {
            var pool = !request.Refresh &&
                       _pools.TryGetValue(key, out var hit) &&
                       DateTime.UtcNow - hit.GeneratedAt < CacheFor
                ? hit
                : null;

            if (pool is null)
            {
                var started = DateTime.UtcNow;
                var exclude = new HashSet<long>(libraryIds.Concat(seeds));
                var related = await store.GetRelatedAsync(seeds, exclude, filters.ContentRatings, ct);
                foreach (var r in related)
                {
                    exclude.Add(long.Parse(r.ProviderId));
                }

                // Prefer semantic ("feel") matches once the embedding index is built; fall back to
                // the genre/tag/author scan while it's still populating (or empty).
                var similar = semantic.IsReady()
                    ? await semantic.GetSimilarAsync(seeds, exclude, PoolSize, filters, request.Obscurity,
                        seedWeight.Count > 0 ? seedWeight : null, request.Diversity,
                        coGraph: coGraph, coRead: coRead, taste: tasteVectors, ct: ct)
                    : [];
                var mode = similar.Count > 0 ? "semantic" : "genre";
                if (similar.Count == 0)
                {
                    similar = await store.GetSimilarAsync(seeds, exclude, PoolSize, filters, ct);
                }

                logger.LogInformation(
                    "Computed recommendations for {SeedCount} seed(s) in {Elapsed:F1}s: {Related} related, {Similar} similar ({Mode})",
                    seeds.Count, (DateTime.UtcNow - started).TotalSeconds, related.Count, similar.Count, mode);

                pool = new RecommendationsResult(related, similar, DateTime.UtcNow);
                Store(key, pool);
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

    /// <summary>
    /// Whether the co-recommendation channel may contribute. Read per request, same as
    /// <see cref="SeedWeightService"/>'s own settings read and for the same reason: the switch should land on
    /// the next uncached pool rather than needing a restart.
    /// <para>
    /// Default on. With no artifact installed this is moot — the graph cache hands back null and
    /// the channel contributes nothing either way.
    /// </para>
    /// </summary>
    private async Task<bool> CoGraphEnabledAsync(CancellationToken ct)
    {
        var value = await settings.GetAsync(SettingKeys.RecommendationsCoGraph, ct);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same, for the co-read channel. Independently switchable; see the setting.</summary>
    private async Task<bool> CoReadEnabledAsync(CancellationToken ct)
    {
        var value = await settings.GetAsync(SettingKeys.RecommendationsCoRead, ct);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same, for the behavioural channel. Independently switchable; see the setting.</summary>
    private async Task<bool> TasteEnabledAsync(CancellationToken ct)
    {
        var value = await settings.GetAsync(SettingKeys.RecommendationsTasteVectors, ct);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Caches a pool, dropping expired entries first and then the oldest if the slots are still full.
    /// Called under <see cref="_lock"/>.
    /// </summary>
    private void Store(string key, RecommendationsResult pool)
    {
        _pools[key] = pool;
        if (_pools.Count <= CacheSlots)
        {
            return;
        }

        foreach (var stale in _pools
                     .Where(kv => DateTime.UtcNow - kv.Value.GeneratedAt >= CacheFor)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _pools.Remove(stale);
        }

        while (_pools.Count > CacheSlots)
        {
            _pools.Remove(_pools.MinBy(kv => kv.Value.GeneratedAt).Key);
        }
    }

    private static string FilterKey(RecommendationFilters f) =>
        $"{f.YearMin}-{f.YearMax}-{f.MinRating}-{string.Join('.', f.Types ?? [])}-{string.Join('.', f.Statuses ?? [])}" +
        $"-{string.Join('.', f.Genres ?? [])}-{f.MinChapters}-{f.MaxChapters}-{string.Join('.', f.Tags ?? [])}" +
        $"-{string.Join('.', f.ContentRatings ?? [])}";
}
