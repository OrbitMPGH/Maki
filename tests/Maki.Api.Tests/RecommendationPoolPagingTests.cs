using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Metadata.CoRead;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.RecoGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Paginated compact-pool behaviour on <see cref="RecommendationService"/>: deep pools up to
/// <see cref="RecommendationService.PoolSize"/>, lazy per-page hydration, build/dump versioning,
/// and mid-scroll feedback suppression without restarting the pager.
/// </summary>
public class RecommendationPoolPagingTests : IDisposable
{
    private readonly TestDb _db = new();

    public RecommendationPoolPagingTests() => _db.SeedUser();

    public void Dispose() => _db.Dispose();

    private sealed class CompactRecommender(int totalPicks) : SemanticRecommender(
        new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base),
        new MangaBakaDumpOptions("", ""),
        new EmbeddingStore(new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base)),
        null!,
        null!,
        RecoGraphTuning.Default,
        null!,
        CoReadTuning.Default,
        NullLogger<SemanticRecommender>.Instance)
    {
        public int Builds { get; private set; }
        public int LastLimit { get; private set; }
        public int LastDepth { get; private set; }

        public override bool IsReady() => true;

        public override bool SupportsCompactPool => true;

        public override Task<IReadOnlyDictionary<long, int>> FranchisesAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, int>>(new Dictionary<long, int>());

        public override Task<RankedRecommendationPool> GetSimilarPoolAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, int depth, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null,
            IReadOnlyDictionary<long, double>? avoidWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, bool ease = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null, double? graphWeight = null,
            IReadOnlyDictionary<long, double>? demote = null, bool common = false, bool lanes = false,
            bool stampFranchises = true,
            CancellationToken ct = default)
        {
            Builds++;
            LastLimit = limit;
            LastDepth = depth;
            var take = Math.Min(totalPicks, depth);
            var picks = Enumerable.Range(1, take)
                .Select(i => new RankedRecommendationPick(
                    SeriesId: 1000 + i,
                    FallbackRating: 80,
                    MatchedGenres: ["Action"],
                    CandidateTags: ["Time Loop"],
                    AuthorMatch: false,
                    CoRecommended: false,
                    CoRead: false,
                    TasteMatch: false,
                    FeelSeedIds: [1L]))
                .ToList();
            var seeds = new Dictionary<long, ReasonSeed>
            {
                [1L] = new("1", "Seed One", "1.jpg"),
            };
            return Task.FromResult(new RankedRecommendationPool(picks, seeds));
        }
    }

    private sealed class HydratingStore : MangaBakaLocalStore
    {
        public HydratingStore()
            : base(new MangaBakaDumpOptions("", ""), NullLogger<MangaBakaLocalStore>.Instance)
        {
        }

        public string CurrentDumpStamp { get; set; } = "dump-v1";
        public List<int> HydratedBatchSizes { get; } = [];

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override string DumpStamp() => CurrentDumpStamp;

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>([]);

        public override Task<IReadOnlySet<long>> GetOwnedEditionIdsAsync(
            IReadOnlyCollection<long> owned, IReadOnlyCollection<long> candidates, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

        public override Task<IReadOnlyList<MangaBakaRecommendation>> HydratePicksAsync(
            IReadOnlyList<RankedRecommendationPick> picks,
            IReadOnlyDictionary<long, ReasonSeed> seeds,
            CancellationToken ct = default)
        {
            HydratedBatchSizes.Add(picks.Count);
            IReadOnlyList<MangaBakaRecommendation> result = picks
                .Select(p =>
                {
                    var feelId = p.BalancedFeelSeedId ?? p.PrimaryFeelSeedId;
                    var seed = feelId is { } id && seeds.TryGetValue(id, out var found) ? found : null;
                    return new MangaBakaRecommendation(
                        p.SeriesId.ToString(),
                        $"Series {p.SeriesId}",
                        null,
                        2024,
                        null,
                        SeriesStatus.Ongoing,
                        p.FallbackRating,
                        40,
                        p.MatchedGenres,
                        p.CandidateTags.Take(4).ToList(),
                        p.AuthorMatch,
                        null,
                        null,
                        BecauseOfTitle: seed?.Title,
                        BecauseOfId: seed?.Id,
                        BecauseOfThumbUrl: seed?.ThumbUrl,
                        FeelSeeds: seed is null ? null : [seed]);
                })
                .ToList();
            return Task.FromResult(result);
        }
    }

    private (RecommendationService Service, CompactRecommender Recommender, HydratingStore Store) Create(int totalPicks = 1000)
    {
        var recommender = new CompactRecommender(totalPicks);
        var store = new HydratingStore();
        var settings = new FakeAppSettings();
        var service = new RecommendationService(
            _db.ScopeFactory(),
            store,
            recommender,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance);
        return (service, recommender, store);
    }

    [Fact]
    public async Task Pages_up_to_1000_hydrate_lazily_40_at_a_time()
    {
        var (service, recommender, store) = Create(1000);
        var user = new TestCurrentUser(1);

        var page0 = await service.GetAsync(new RecommendationRequest(SeedIds: [1], Page: 0), user);
        var page24 = await service.GetAsync(
            new RecommendationRequest(SeedIds: [1], Page: 24, PoolVersion: page0.PoolVersion), user);

        Assert.Equal(1, recommender.Builds);
        Assert.Equal(200, recommender.LastLimit);
        Assert.Equal(RecommendationService.PoolSize, recommender.LastDepth);
        Assert.Equal(1000, page0.TotalSimilar);
        Assert.True(page0.HasMore);
        Assert.Equal(40, page0.Similar.Count);
        Assert.Equal("1001", page0.Similar[0].ProviderId);

        Assert.False(page24.RestartRequired);
        Assert.False(page24.HasMore);
        Assert.Equal(40, page24.Similar.Count);
        Assert.Equal("1961", page24.Similar[0].ProviderId);
        Assert.Equal("2000", page24.Similar[^1].ProviderId);
        Assert.Equal([40, 40], store.HydratedBatchSizes);
    }

    [Fact]
    public async Task Hiding_a_title_mid_scroll_keeps_PoolVersion_and_does_not_restart_or_duplicate()
    {
        var (service, recommender, _) = Create(120);
        var user = new TestCurrentUser(1);

        var page0 = await service.GetAsync(new RecommendationRequest(SeedIds: [1], Page: 0), user);
        var hiddenId = long.Parse(page0.Similar[5].ProviderId);
        using (var db = _db.NewContext())
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1,
                ProviderId = hiddenId,
                Suppression = RecommendationSuppression.Hidden,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var page1 = await service.GetAsync(
            new RecommendationRequest(SeedIds: [1], Page: 1, PoolVersion: page0.PoolVersion), user);

        Assert.Equal(1, recommender.Builds);
        Assert.False(page1.RestartRequired);
        Assert.Equal(page0.PoolVersion, page1.PoolVersion);
        Assert.Equal(1, page1.Page);
        Assert.Equal(119, page1.TotalSimilar);
        Assert.DoesNotContain(page1.Similar, p => p.ProviderId == hiddenId.ToString());
        Assert.Empty(page0.Similar.Select(p => p.ProviderId).Intersect(page1.Similar.Select(p => p.ProviderId)));
    }

    [Fact]
    public async Task Refresh_on_same_key_changes_PoolVersion_and_restarts_stale_pager()
    {
        var (service, recommender, _) = Create(120);
        var user = new TestCurrentUser(1);

        var first = await service.GetAsync(new RecommendationRequest(SeedIds: [1], Page: 0), user);
        var refreshed = await service.GetAsync(new RecommendationRequest(SeedIds: [1], Page: 0, Refresh: true), user);
        var stalePage = await service.GetAsync(
            new RecommendationRequest(SeedIds: [1], Page: 2, PoolVersion: first.PoolVersion), user);

        Assert.Equal(2, recommender.Builds);
        Assert.NotEqual(first.PoolVersion, refreshed.PoolVersion);
        Assert.True(stalePage.RestartRequired);
        Assert.Equal(0, stalePage.Page);
        Assert.Equal(refreshed.PoolVersion, stalePage.PoolVersion);
    }

    [Fact]
    public async Task Dump_swap_invalidates_pool_and_restarts_paging()
    {
        var (service, recommender, store) = Create(120);
        var user = new TestCurrentUser(1);

        var page0 = await service.GetAsync(new RecommendationRequest(SeedIds: [1], Page: 0), user);
        store.CurrentDumpStamp = "dump-v2";

        var next = await service.GetAsync(
            new RecommendationRequest(SeedIds: [1], Page: 1, PoolVersion: page0.PoolVersion), user);

        Assert.Equal(2, recommender.Builds);
        Assert.True(next.RestartRequired);
        Assert.Equal(0, next.Page);
        Assert.NotEqual(page0.PoolVersion, next.PoolVersion);
    }
}
