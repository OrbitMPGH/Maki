using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>One page of recommendations. <see cref="HasMore"/> means a deeper page exists in the cached pool.</summary>
public record RecommendationsResult(
    IReadOnlyList<MangaBakaRecommendation> Related,
    IReadOnlyList<MangaBakaRecommendation> Similar,
    DateTime GeneratedAt,
    int Page = 0,
    bool HasMore = false,
    string? PoolVersion = null,
    bool RestartRequired = false,
    int TotalSimilar = 0,
    int? NextCursor = null);

/// <summary>
/// Recommendation request. <see cref="SeedIds"/> are MangaBaka ids to base the picks on
/// (empty = the whole library); the rest constrain candidates. Any owned series is always
/// excluded from results, whether or not it's a seed. <see cref="Page"/> and <see cref="Cursor"/>
/// page through the cached similar pool ("Show more") without recomputing it.
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
    double Diversity = 0,
    string? PoolVersion = null,
    int? Cursor = null);

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
    ILogger<RecommendationService> logger,
    KnownTitleTuning? knownTitles = null,
    Maki.Metadata.PageShare.PageShareCache? pageShareCache = null)
{
    private readonly KnownTitleTuning _known = knownTitles ?? KnownTitleTuning.Default;

    private const int PageSize = 40;
    private const int CalibrationLimit = 200;

    /// <summary>Matches the custom rails' seed cap, so a bound request cannot build a megabyte cache key.</summary>
    internal const int MaxSeeds = 100;

    /// <summary>The page ceiling; the pool is up to <see cref="PoolSize"/> rows, so no real pager gets near it.</summary>
    internal const int MaxPage = 1000;

    /// <summary>Clamps the dials, seed count, page and cursor of a request bound from a POST body.</summary>
    internal static RecommendationRequest Normalize(RecommendationRequest request) => request with
    {
        SeedIds = request.SeedIds?.Distinct().Take(MaxSeeds).ToList(),
        Obscurity = Math.Clamp(double.IsFinite(request.Obscurity) ? request.Obscurity : 0, -1, 1),
        Diversity = Math.Clamp(double.IsFinite(request.Diversity) ? request.Diversity : 0, 0, 1),
        Page = Math.Clamp(request.Page, 0, MaxPage),
        Cursor = request.Cursor is int cursor ? Math.Clamp(cursor, 0, PoolSize) : null,
    };

    internal const int PoolSize = 1000;
    private const int FallbackPoolSize = 200;

    /// <summary>
    /// How many distinct pools to keep. More than one because the cache key carries the caller's
    /// library, ratings and derived taste weights, so on a multi-user instance every person has their
    /// own key — a single slot would thrash between them and recompute a full index scan per request.
    /// Two per person, in fact: the Recommended tab seeds on the whole library while Discover's
    /// recent-activity rail seeds on the last few series read, and the rail is on the tab people land
    /// on. Compact pools are small enough to keep the same slot budgets while holding 1,000 picks.
    /// </summary>
    private const int CacheSlots = 16;

    /// <summary>
    /// Custom rails' own budget, separate from <see cref="CacheSlots"/> so they cannot evict the
    /// pools above. Each rail with its own filters is its own pool, and a reader can have several.
    /// </summary>
    private const int RailCacheSlots = 24;

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    /// <summary>
    /// How far apart two picks from one same-work component have to sit in the similar pool.
    ///
    /// <para>
    /// Eight because that is roughly what a wide Discover rail shows at once, so a franchise can
    /// put at most two cards in front of somebody without them scrolling. The reported case was
    /// four Nagatoro spin-offs in the first nine cards of "Based on your recent activity", which
    /// reads as the recommender being stuck rather than as six seeds' worth of picks.
    /// </para>
    ///
    /// <para>
    /// Spacing rather than a cap, deliberately. Dropping franchise members outright is
    /// <see cref="RecommenderTuning.MaxPerFranchise"/>, which ships off because it was measured and
    /// it costs relevance: readers who finish something do go on to read the rest of it. Nothing is
    /// dropped here, so every pick the scorer wanted is still in the pool and still reachable by
    /// paging; only the order changes, and only enough to break up a run.
    /// </para>
    /// </summary>
    private const int FranchiseSpacing = 8;

    private readonly object _poolsGate = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, PoolBuild> _building = [];
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];
    private long _nextBuildId;

    /// <summary>
    /// One pending pool build and how many readers are waiting on it. When the last one leaves
    /// before the build reaches the scan gate it is cancelled, so a key nobody wants any more
    /// (a slider position passed through, a rail scrolled past) never costs a scan.
    /// </summary>
    internal int PendingBuilds
    {
        get
        {
            lock (_poolsGate)
            {
                return _building.Count;
            }
        }
    }

    private sealed class PoolBuild
    {
        public Task<CachedRecommendationPool> Task { get; set; } = null!;
        public int Waiters { get; set; }
        public bool Scanning { get; set; }
        public CancellationTokenSource Abandoned { get; } = new();
    }
    private readonly RecommendationPoolCache _pools = new(CacheSlots, RailCacheSlots, CacheFor);

    public int ReleaseIdlePools(TimeSpan railIdleFor, TimeSpan interactiveIdleFor)
    {
        lock (_poolsGate)
        {
            return _pools.ReleaseIdle(railIdleFor, interactiveIdleFor);
        }
    }

    /// <param name="scope">
    /// The caller's data scope, applied to the child scope this opens. A singleton creating its own
    /// scope gets a fresh unrestricted <see cref="DataScope"/>, which would seed recommendations from
    /// root folders the caller was never granted and weight them with somebody else's ratings.
    /// </param>
    /// <param name="origin">
    /// Whose cache budget the pool counts against. A parameter rather than a request field because
    /// the request is bound from a POST body and a client must not be able to pick its own budget.
    /// </param>
    public async Task<RecommendationsResult> GetAsync(
        RecommendationRequest request, ICurrentUser scope, CancellationToken ct = default,
        PoolOrigin origin = PoolOrigin.Interactive)
    {
        request = Normalize(request);
        if (!await store.IsAvailableAsync(ct))
        {
            throw new LocalCatalogueUnavailableException("error.recommendation.needsLocalDb");
        }

        // MangaBaka id -> seed weight. A series rated 5 or better gets rating/5.0 (10→2.0, 5→1.0
        // neutral); an unrated one gets whatever its reading history implies, or nothing at all if
        // there is no history to read. Seeds absent from here default to 1.0 in the weighted mean.
        // A rating of 4 or under is not a weak positive seed: it leaves this population entirely and
        // joins snapshot.Avoided, which the recommender subtracts with rather than steers by.
        SeedSnapshot snapshot;
        HashSet<long> suppressed;
        using (var dbScope = scopeFactory.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.Scope.SetUser(scope.UserId, scope.AllRootFolders);
            snapshot = await seedWeights.SnapshotAsync(db, scope, ct);
            suppressed = await RecommendationFeedbackService.SuppressedAsync(db, scope.UserId, ct);
            // A liked title is a seed on the automatic path, and seeds are never picks. Callers that
            // choose their own seeds (recent activity, side interests, custom rails) would otherwise
            // recommend it straight back.
            suppressed.UnionWith(await RecommendationFeedbackService.LikedAsync(db, scope.UserId, ct));
        }

        var seeded = snapshot.Effective;
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
        // Automatic seeds come from the effective population, which is what "stop using this as a
        // taste signal" narrows. An explicitly chosen seed is checked against the observed one
        // instead: asking "more like this" about a title is a deliberate one-off, and refusing it
        // because that title is excluded from the inferred profile answers a question nobody asked.
        // The weights below stay effective either way, so the seed steers at neutral weight and its
        // excluded preference is not quietly reinstated. Only owned titles are checked: a chosen
        // seed that is not on the shelf is a catalogue pick ("more like this" from the Discover
        // hero), and dropping it would leave the request with no seeds at all.
        var hidden = snapshot.Observed.LibraryIds.ToHashSet();
        hidden.ExceptWith(snapshot.Observed.EligibleIds);
        IReadOnlyList<long> seeds = request.SeedIds is { Count: > 0 } chosen
            ? chosen.Where(id => !hidden.Contains(id)).Distinct().OrderBy(id => id).ToList()
            : seeded.EligibleIds;
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
        var ease = await EaseEnabledAsync(ct);
        var pageShare = await PageShareEnabledAsync(ct);
        // The table's generation, not just the switch: a pool built before the first download (or
        // against a replaced table) must not be served for twelve hours, and the pool version
        // derived from this key tells a reader paging the old pool to start over.
        var pageShareKey = pageShare ? $"1.{pageShareCache?.Generation ?? 0}" : "0";
        // Keyed on the inputs, not on who asked. The seed list already carries everything personal
        // that reaches the pool (root-folder visibility, the content ceiling and ignored sources
        // all narrow it before it gets here), so two readers with the same seeds want the same
        // ranked pool. Naming the user instead would give every reader on a shared library a private
        // pool and thrash CacheSlots. Suppression is not in here on purpose: it is a per-reader
        // overlay applied to the finished pool below, so a hide costs a filter rather than a rebuild.
        // The avoided set is an input to the pool, not a per-reader overlay on it like suppression,
        // so it has to be in the key: without it a thumbs down sits invisible behind a 12-hour hit.
        var avoidKey = string.Join(",", snapshot.Avoided
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Key}:{x.Value:F1}"));
        // Titles the reader most likely knows. Both go into the pool and so into the key. An
        // exclusion leaves the scan itself rather than the finished pool, because the fame cap runs
        // inside the scan and a watched title removed afterwards would have used its quota.
        var (knownExcluded, demote) = await KnownAsync(snapshot, ct);
        var demoteKey = demote.Count == 0
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", demote
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value:F2}")))))[..16];
        var knownKey = knownExcluded.Count == 0
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", knownExcluded.Order()))))[..16];
        var lanes = request.SeedIds is not { Count: > 0 } && origin == PoolOrigin.Interactive;
        var baseKey = $"{string.Join(",", seeds)}|lib:{string.Join(",", libraryIds)}|{FilterKey(filters)}" +
                      $"|o:{request.Obscurity:F2}|d:{request.Diversity:F2}|w:{weightKey}" +
                      $"|g:{(coGraph ? 1 : 0)}|c:{(coRead ? 1 : 0)}|t:{(tasteVectors ? 1 : 0)}" +
                      $"|e:{(ease ? 1 : 0)}|p:{pageShareKey}|a:{avoidKey}|k:{demoteKey}|x:{knownKey}" +
                      $"|l:{(lanes ? 1 : 0)}";

        var forceRestart = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var dumpStamp = store.DumpStamp();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{baseKey}|dump:{dumpStamp}")));
            var pool = await GetOrBuildPoolAsync(
                key, origin, request.Refresh && attempt == 0, seeds, libraryIds, filters, request,
                seedWeight, snapshot.Avoided, coGraph, coRead, tasteVectors, ease, pageShare, lanes,
                demote, knownExcluded, dumpStamp, ct);

            // Keyed on inputs (including dump stamp), process instance, and this build's monotonic id,
            // never on feedbackRevision. Process restarts, refresh, cache eviction, TTL expiry or a
            // dump swap restart paging; hiding or dismissing a card keeps the same version.
            var version = $"{key[..16]}:{_instanceId}:{pool.BuildId}";
            var restart = forceRestart || (request.PoolVersion is { Length: > 0 } paging && paging != version);
            var page = restart ? 0 : Math.Max(0, request.Page);
            var cursor = restart ? null : request.Cursor;
            var relatedVisible = pool.Related.Where(p => !suppressed.Contains(CatalogueId(p))).ToList();

            if (pool.CompactSimilar is { } compact)
            {
                var (visiblePrefix, skipCount, takeCount, nextCursor, hasMore, totalVisible) =
                    SliceStablePool(compact, static p => p.SeriesId, suppressed, page, cursor);
                var slice = RecommendationReasons.BalanceSlice(visiblePrefix, skipCount, takeCount);
                var hydrated = await store.HydratePicksAsync(slice, pool.Seeds!, ct);
                if (attempt == 0 && !string.Equals(store.DumpStamp(), pool.DumpStamp, StringComparison.Ordinal))
                {
                    forceRestart = true;
                    continue;
                }

                return new RecommendationsResult(
                    relatedVisible,
                    hydrated,
                    pool.GeneratedAt,
                    Page: page,
                    HasMore: hasMore,
                    PoolVersion: version,
                    RestartRequired: restart,
                    TotalSimilar: totalVisible,
                    NextCursor: nextCursor);
            }

            var prehydrated = pool.HydratedSimilar!;
            var (hydratedPrefix, hSkip, hTake, hNextCursor, hHasMore, hTotal) =
                SliceStablePool(prehydrated, CatalogueId, suppressed, page, cursor);
            var balanced = RecommendationReasons.Balance(hydratedPrefix);
            return new RecommendationsResult(
                relatedVisible,
                balanced.Skip(hSkip).Take(hTake).ToList(),
                pool.GeneratedAt,
                Page: page,
                HasMore: hHasMore,
                PoolVersion: version,
                RestartRequired: restart,
                TotalSimilar: hTotal,
                NextCursor: hNextCursor);
        }

        throw new InvalidOperationException("Unreachable recommendation pool retry state.");
    }

    /// <summary>
    /// Pages the build-time pool by stable rank <paramref name="cursor"/> when supplied (falling back
    /// to <paramref name="page"/> * <see cref="PageSize"/> over visible items for cursorless callers).
    /// Anchoring to the pool index keeps hides and expiring dismissals on earlier pages from skipping
    /// or duplicating cards at page boundaries.
    /// </summary>
    private static (List<T> VisiblePrefix, int Skip, int Take, int NextCursor, bool HasMore, int TotalVisible)
        SliceStablePool<T>(
            IReadOnlyList<T> pool,
            Func<T, long> idOf,
            HashSet<long> suppressed,
            int page,
            int? cursor)
    {
        var startIndex = cursor is int c ? Math.Clamp(c, 0, pool.Count) : (page == 0 ? 0 : -1);
        var visiblePrefix = new List<T>();
        var skip = 0;
        var take = 0;
        var nextCursor = pool.Count;
        var hasMore = false;
        var totalVisible = 0;
        var legacySkip = page * PageSize;

        for (var i = 0; i < pool.Count; i++)
        {
            var item = pool[i];
            if (suppressed.Contains(idOf(item)))
            {
                continue;
            }

            totalVisible++;
            if (startIndex >= 0)
            {
                if (i < startIndex)
                {
                    visiblePrefix.Add(item);
                    skip++;
                }
                else if (take < PageSize)
                {
                    visiblePrefix.Add(item);
                    take++;
                    nextCursor = i + 1;
                }
                else
                {
                    hasMore = true;
                }
            }
            else
            {
                if (skip < legacySkip)
                {
                    visiblePrefix.Add(item);
                    skip++;
                }
                else if (take < PageSize)
                {
                    visiblePrefix.Add(item);
                    take++;
                    nextCursor = i + 1;
                }
                else
                {
                    hasMore = true;
                }
            }
        }

        return (visiblePrefix, skip, take, nextCursor, hasMore, totalVisible);
    }

    private async Task<CachedRecommendationPool> GetOrBuildPoolAsync(
        string key, PoolOrigin origin, bool refresh, IReadOnlyList<long> seeds, IReadOnlyList<long> libraryIds,
        RecommendationFilters filters, RecommendationRequest request,
        IReadOnlyDictionary<long, double> seedWeight, IReadOnlyDictionary<long, double> avoided,
        bool coGraph, bool coRead, bool tasteVectors, bool ease, bool pageShare, bool lanes,
        IReadOnlyDictionary<long, double> demote, IReadOnlySet<long> knownExcluded, string dumpStamp,
        CancellationToken ct)
    {
        // Hits only need the pool lookup, so they never queue behind a scan. Builds are single-flight
        // per key and run one at a time behind _scanGate. A scan that has started finishes on no
        // caller's token, so a reader who leaves mid-scan neither throws the work away nor makes the
        // next reader start it again; one still queued when its last reader leaves is dropped.
        CachedRecommendationPool? pool = null;
        PoolBuild? build = null;
        lock (_poolsGate)
        {
            if (!refresh && _pools.TryGet(key, origin, out var hit))
            {
                pool = hit;
            }
            else
            {
                if (!_building.TryGetValue(key, out build))
                {
                    var created = new PoolBuild();
                    created.Task = Task.Run(() => BuildPoolAsync(
                        key, created, origin, seeds, libraryIds, filters, request, seedWeight, avoided,
                        coGraph, coRead, tasteVectors, ease, pageShare, lanes, demote, knownExcluded, dumpStamp));
                    _building[key] = created;
                    build = created;
                }

                build.Waiters++;
            }
        }

        if (pool is not null)
        {
            return pool;
        }

        try
        {
            return await build!.Task.WaitAsync(ct);
        }
        finally
        {
            lock (_poolsGate)
            {
                if (--build!.Waiters == 0 && !build.Scanning && !build.Task.IsCompleted)
                {
                    build.Abandoned.Cancel();
                    if (_building.TryGetValue(key, out var current) && ReferenceEquals(current, build))
                    {
                        _building.Remove(key);
                    }
                }
            }
        }
    }

    private async Task<CachedRecommendationPool> BuildPoolAsync(
        string key, PoolBuild self, PoolOrigin origin, IReadOnlyList<long> seeds, IReadOnlyList<long> libraryIds,
        RecommendationFilters filters, RecommendationRequest request,
        IReadOnlyDictionary<long, double> seedWeight, IReadOnlyDictionary<long, double> avoided,
        bool coGraph, bool coRead, bool tasteVectors, bool ease, bool pageShare, bool lanes,
        IReadOnlyDictionary<long, double> demote, IReadOnlySet<long> known, string dumpStamp)
    {
        var entered = false;
        try
        {
            await _scanGate.WaitAsync(self.Abandoned.Token);
            entered = true;
            lock (_poolsGate)
            {
                self.Abandoned.Token.ThrowIfCancellationRequested();
                self.Scanning = true;
            }

            var started = DateTime.UtcNow;
            var buildId = Interlocked.Increment(ref _nextBuildId);
            var exclude = new HashSet<long>(libraryIds.Concat(seeds));
            var related = await store.GetRelatedAsync(seeds, exclude, filters.ContentRatings, CancellationToken.None);
            foreach (var r in related)
            {
                exclude.Add(long.Parse(r.ProviderId));
            }

            // After the relations, which keep known titles: a sequel of something on the shelf is
            // expected to be familiar.
            exclude.UnionWith(known);

            var owned = libraryIds.Concat(seeds).Concat(known).ToHashSet();

            // Prefer semantic ("feel") matches once the embedding index is built; fall back to
            // the genre/tag/author scan while it's still populating (or empty).
            if (semantic.IsReady() && semantic.SupportsCompactPool)
            {
                var compact = await semantic.GetSimilarPoolAsync(
                    seeds, exclude, CalibrationLimit, PoolSize, filters, request.Obscurity,
                    seedWeight.Count > 0 ? seedWeight : null,
                    avoided.Count > 0 ? avoided : null, request.Diversity,
                    coGraph: coGraph, coRead: coRead, taste: tasteVectors, ease: ease,
                    demote: demote.Count > 0 ? demote : null, common: pageShare, lanes: lanes,
                    stampFranchises: true, ct: CancellationToken.None);
                if (compact.Picks.Count > 0)
                {
                    var picks = compact.Picks;
                    var editions = await store.GetOwnedEditionIdsAsync(
                        owned, picks.Select(p => p.SeriesId).ToList(), CancellationToken.None);
                    if (editions.Count > 0)
                    {
                        picks = picks.Where(p => !editions.Contains(p.SeriesId)).ToList();
                    }

                    logger.LogInformation(
                        "Computed recommendations for {SeedCount} seed(s) in {Elapsed:F1}s: {Related} related, {Similar} similar (semantic)",
                        seeds.Count, (DateTime.UtcNow - started).TotalSeconds, related.Count, picks.Count);

                    if (related.Count > 0)
                    {
                        var relatedFranchises = await semantic.FranchisesAsync(
                            related.Select(CatalogueId).Where(id => id > 0).ToList(), CancellationToken.None);
                        related = WithFranchises(related, relatedFranchises);
                    }

                    var spreadPicks = Spread(picks);
                    var compactPool = new CachedRecommendationPool(
                        related, spreadPicks, compact.Seeds, HydratedSimilar: null, DateTime.UtcNow, buildId, dumpStamp);
                    lock (_poolsGate)
                    {
                        _pools.Store(key, compactPool, origin);
                    }

                    return compactPool;
                }
            }

            var similar = semantic.IsReady() && !semantic.SupportsCompactPool
                ? await semantic.GetSimilarAsync(seeds, exclude, CalibrationLimit, filters, request.Obscurity,
                    seedWeight.Count > 0 ? seedWeight : null,
                    avoided.Count > 0 ? avoided : null, request.Diversity,
                    coGraph: coGraph, coRead: coRead, taste: tasteVectors, ease: ease,
                    demote: demote.Count > 0 ? demote : null, common: pageShare, lanes: lanes,
                    ct: CancellationToken.None)
                : [];
            var mode = similar.Count > 0 ? "semantic" : "genre";
            if (similar.Count == 0)
            {
                // The fallback scan has no vectors, so there is nothing to measure resemblance
                // against and the avoid channel simply cannot apply here.
                similar = await store.GetSimilarAsync(seeds, exclude, FallbackPoolSize, filters, CancellationToken.None);
            }

            var hydratedEditions = await store.GetOwnedEditionIdsAsync(
                owned, similar.Select(CatalogueId).Where(id => id > 0).ToList(),
                CancellationToken.None);
            if (hydratedEditions.Count > 0)
            {
                similar = similar.Where(p => !hydratedEditions.Contains(CatalogueId(p))).ToList();
            }

            logger.LogInformation(
                "Computed recommendations for {SeedCount} seed(s) in {Elapsed:F1}s: {Related} related, {Similar} similar ({Mode})",
                seeds.Count, (DateTime.UtcNow - started).TotalSeconds, related.Count, similar.Count, mode);

            // One index pass for both lists, so a relation and a similar pick that are the same
            // work carry the same component and the surfaces can see that they are.
            var franchises = await semantic.FranchisesAsync(
                related.Concat(similar).Select(CatalogueId).Where(id => id > 0).ToList(), CancellationToken.None);
            related = WithFranchises(related, franchises);
            similar = Spread(WithFranchises(similar, franchises));

            var pool = new CachedRecommendationPool(
                related, CompactSimilar: null, Seeds: null, similar, DateTime.UtcNow, buildId, dumpStamp);
            lock (_poolsGate)
            {
                _pools.Store(key, pool, origin);
            }

            return pool;
        }
        finally
        {
            lock (_poolsGate)
            {
                if (_building.TryGetValue(key, out var current) && ReferenceEquals(current, self))
                {
                    _building.Remove(key);
                }
            }

            if (entered)
            {
                _scanGate.Release();
            }
        }
    }

    /// <summary>
    /// The titles <see cref="KnownTitleTuning"/> takes out of the similar picks, and the score it
    /// takes off the ones it only ranks lower. Both empty at the shipped tuning, and for a reader
    /// with no watch list.
    /// </summary>
    private async Task<(IReadOnlySet<long> Excluded, IReadOnlyDictionary<long, double> Demote)> KnownAsync(
        SeedSnapshot snapshot, CancellationToken ct)
    {
        var excluded = new HashSet<long>();
        var demote = new Dictionary<long, double>();
        var watched = snapshot.WatchedAnime;
        if (!_known.Active || watched.Count == 0)
        {
            return (excluded, demote);
        }

        if (_known.WatchedAnime == KnownTitleMode.Exclude)
        {
            excluded.UnionWith(watched);
        }
        else if (_known.WatchedAnime == KnownTitleMode.Demote && _known.WatchedDemotion > 0)
        {
            foreach (var id in watched)
            {
                demote[id] = _known.WatchedDemotion;
            }
        }

        if (_known.UnwatchedWeight > 0)
        {
            var aired = await store.GetAiredAnimeRanksAsync(_known.UnwatchedMaxRank, ct);
            var watchedFamous = aired.Keys.Count(watched.Contains);
            foreach (var (id, rank) in aired)
            {
                if (watched.Contains(id))
                {
                    continue;
                }

                var strength = _known.UnwatchedStrength(rank, watchedFamous, aired.Count);
                if (strength > 0)
                {
                    demote[id] = _known.UnwatchedWeight * strength;
                }
            }
        }

        return (excluded, demote);
    }

    /// <summary>The pick's MangaBaka id, or 0 when it does not parse (nothing in the dump).</summary>
    private static long CatalogueId(MangaBakaRecommendation pick) =>
        long.TryParse(pick.ProviderId, out var id) ? id : 0;

    /// <summary>
    /// Stamps each pick with its same-work component, where <paramref name="franchises"/> has one.
    /// Picks the index does not place keep a null <c>FranchiseId</c>, which reads as "in no
    /// franchise" and never groups with another null.
    /// </summary>
    private static IReadOnlyList<MangaBakaRecommendation> WithFranchises(
        IReadOnlyList<MangaBakaRecommendation> picks, IReadOnlyDictionary<long, int> franchises) =>
        franchises.Count == 0
            ? picks
            : picks
                .Select(p => franchises.TryGetValue(CatalogueId(p), out var franchise)
                    ? p with { FranchiseId = franchise }
                    : p)
                .ToList();

    /// <summary>
    /// Re-orders a ranked list so no two members of one franchise land within
    /// <see cref="FranchiseSpacing"/> positions of each other, keeping everything and keeping the
    /// relative order inside each franchise.
    ///
    /// <para>
    /// Greedy: take the best-ranked pick whose franchise has had room since its last one, and if
    /// none qualifies take the best-ranked pick regardless. That fallback is what stops a tail made
    /// entirely of one franchise from stalling, and it is also why this cannot promote anything past
    /// a pick with no franchise: those are never held back.
    /// </para>
    /// <para>
    /// A pick the side-taste top-up inserted is never placed above its
    /// <see cref="MangaBakaRecommendation.LaneFloor"/>, so the visible first picks are the ones the
    /// page had without the top-up. Every other pick moves exactly as before.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<MangaBakaRecommendation> Spread(
        IReadOnlyList<MangaBakaRecommendation> ranked) =>
        Spread(ranked, static p => p.FranchiseId, static p => p.LaneFloor);

    internal static IReadOnlyList<RankedRecommendationPick> Spread(
        IReadOnlyList<RankedRecommendationPick> ranked) =>
        Spread(ranked, static p => p.FranchiseId, static p => p.LaneFloor);

    private static IReadOnlyList<T> Spread<T>(
        IReadOnlyList<T> ranked, Func<T, int?> franchiseOf, Func<T, int> laneFloorOf)
    {
        if (ranked.Count < 2)
        {
            return ranked;
        }

        var remaining = new LinkedList<T>(ranked);
        var placedAt = new Dictionary<int, int>();
        var ordered = new List<T>(ranked.Count);

        while (remaining.First is { } head)
        {
            LinkedListNode<T>? chosen = null;
            LinkedListNode<T>? fallback = null;
            for (LinkedListNode<T>? node = head; node is not null; node = node.Next)
            {
                if (laneFloorOf(node.Value) > ordered.Count)
                {
                    continue;
                }

                fallback ??= node;
                if (franchiseOf(node.Value) is not int franchise
                    || !placedAt.TryGetValue(franchise, out var last)
                    || ordered.Count - last >= FranchiseSpacing)
                {
                    chosen = node;
                    break;
                }
            }

            chosen ??= fallback ?? head;
            if (franchiseOf(chosen.Value) is int placed)
            {
                placedAt[placed] = ordered.Count;
            }

            ordered.Add(chosen.Value);
            remaining.Remove(chosen);
        }

        return ordered;
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

    /// <summary>The same, for the EASE channel (<c>recommendations.ease</c>).</summary>
    private async Task<bool> EaseEnabledAsync(CancellationToken ct)
    {
        var value = await settings.GetAsync(SettingKeys.RecommendationsEase, ct);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same, for the page-share penalty (<c>recommendations.pageshare</c>).</summary>
    private async Task<bool> PageShareEnabledAsync(CancellationToken ct)
    {
        var value = await settings.GetAsync(SettingKeys.RecommendationsPageShare, ct);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static string FilterKey(RecommendationFilters f) =>
        $"{f.YearMin}-{f.YearMax}-{f.MinRating}-{KeyPart.List(f.Types)}-{KeyPart.List(f.Statuses)}" +
        $"-{KeyPart.List(f.Genres)}-{f.MinChapters}-{f.MaxChapters}-{KeyPart.List(f.Tags)}" +
        $"-{KeyPart.List(f.ContentRatings)}-{CatalogueRules.Key(f.Rules)}-{CatalogueRules.TermsKey(f.Hidden)}" +
        $"-{CatalogueCredits.Key(f.Credits)}";
}
