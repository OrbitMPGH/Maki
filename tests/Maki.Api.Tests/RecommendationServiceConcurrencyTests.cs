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
/// How pool builds share the service: a warm hit must not queue behind somebody else's scan, one key
/// is built once however many readers ask for it, and a reader who leaves does not abort the scan.
/// </summary>
public class RecommendationServiceConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TestDb _db = new();

    public RecommendationServiceConcurrencyTests() => _db.SeedUser();

    public void Dispose() => _db.Dispose();

    /// <summary>Holds any scan seeded with <see cref="Blocked"/> until the test releases it.</summary>
    private sealed class GatedRecommender() : SemanticRecommender(
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
        public const long Blocked = 999;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _scans;
        public int Scans => _scans;

        public override bool IsReady() => true;

        public override Task<IReadOnlyDictionary<long, int>> FranchisesAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, int>>(new Dictionary<long, int>());

        public override async Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null,
            IReadOnlyDictionary<long, double>? avoidWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _scans);
            if (seedIds.Contains(Blocked))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }

            return [new("77", "Title", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null)];
        }
    }

    private sealed class EmptyStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>([]);
    }

    private (RecommendationService Service, GatedRecommender Recommender) Service()
    {
        var recommender = new GatedRecommender();
        var settings = new FakeAppSettings();
        return (new RecommendationService(
            _db.ScopeFactory(),
            new EmptyStore(),
            recommender,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance), recommender);
    }

    private static RecommendationRequest Seeded(long seed) => new(SeedIds: [seed]);

    [Fact]
    public async Task A_warm_hit_does_not_wait_for_another_keys_scan()
    {
        var (service, recommender) = Service();
        var user = new TestCurrentUser(1);
        await service.GetAsync(Seeded(1), user);

        var blocked = service.GetAsync(Seeded(GatedRecommender.Blocked), user);
        await recommender.Entered.Task.WaitAsync(Timeout);

        var warm = await service.GetAsync(Seeded(1), user).WaitAsync(Timeout);

        Assert.Single(warm.Similar);
        Assert.False(blocked.IsCompleted);
        recommender.Release.SetResult();
        await blocked.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Readers_asking_for_one_cold_key_share_a_scan_that_survives_one_leaving()
    {
        var (service, recommender) = Service();
        var user = new TestCurrentUser(1);

        using var cts = new CancellationTokenSource();
        var leaving = service.GetAsync(Seeded(GatedRecommender.Blocked), user, cts.Token);
        await recommender.Entered.Task.WaitAsync(Timeout);
        var staying = service.GetAsync(Seeded(GatedRecommender.Blocked), user);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        recommender.Release.SetResult();
        Assert.Single((await staying.WaitAsync(Timeout)).Similar);
        Assert.Single((await service.GetAsync(Seeded(GatedRecommender.Blocked), user)).Similar);
        Assert.Equal(1, recommender.Scans);
    }
}
