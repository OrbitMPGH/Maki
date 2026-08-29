using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The caching around the single-seed recommender. This is the whole point of the service — the scan
/// itself is <c>SemanticRecommender</c>'s and is tested there — because an uncached rail would re-scan
/// the vector index on every series page visit, and because routing it through
/// <c>RecommendationService</c> instead would evict Discover's one shared pool each time.
/// </summary>
public class SimilarSeriesServiceTests
{
    /// <summary>Counts scans, so a test can tell a cache hit from a recomputation.</summary>
    private sealed class CountingRecommender(bool ready = true) : SemanticRecommender(
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
        public int Calls;
        public TaskCompletionSource? Gate;
        public EmbeddingMath.Weights? LastWeights;
        public bool SawWeightsOverride;

        public override bool IsReady() => ready;

        public override async Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            LastWeights = weights;
            SawWeightsOverride |= weights is not null;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return [Recommendation("77")];
        }
    }

    private static MangaBakaRecommendation Recommendation(string providerId) =>
        new(providerId, "Title", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null);

    private static SimilarSeriesService Service(SemanticRecommender recommender) =>
        new(recommender, new FakeAppSettings(), NullLogger<SimilarSeriesService>.Instance);

    [Fact]
    public async Task The_rail_asks_for_the_same_channel_weights_Discover_does()
    {
        // This has now been true, then not, then true again, so it is worth saying why rather than
        // only what. The rail's original reduced Genre/Author vector compensated for a genre channel
        // whose scale moved with the seed count, and went away when that channel was normalized. A
        // reduced tag weight replaced it for a while on a real measurement: at TagCandidateNormPower
        // 1.0 the rail lost 0.0063 nDCG@40 at Discover's tag weight (95% [-0.0108, -0.0020]), a one
        // seed tag profile being that series' tag list rather than an aggregate.
        //
        // Damping the candidate norm to 0.75 addressed that at the source, and re-swept the override
        // no longer paid for itself: +0.0026 against just using the default, 95% [-0.0007, +0.0061].
        // An unmeasured divergence between the two surfaces is exactly what this test exists to
        // stop, so it went away and the assertion is back to what it always was.
        var recommender = new CountingRecommender();

        await Service(recommender).GetAsync(1, ["safe"]);

        Assert.False(recommender.SawWeightsOverride);
        Assert.Null(recommender.LastWeights);
    }

    [Fact]
    public async Task An_unbuilt_index_yields_an_empty_rail_rather_than_a_dump_scan()
    {
        // The genre fallback is a full scan of the ~3 GB dump. Fine behind Discover's 12-hour cache,
        // not fine on a page load, so the rail simply stays empty until the index exists.
        var recommender = new CountingRecommender(ready: false);

        Assert.Empty(await Service(recommender).GetAsync(1, ["safe"]));
        Assert.Equal(0, recommender.Calls);
    }

    [Fact]
    public async Task A_second_visit_to_the_same_series_is_served_from_cache()
    {
        var recommender = new CountingRecommender();
        var service = Service(recommender);

        var first = await service.GetAsync(1, ["safe"]);
        var second = await service.GetAsync(1, ["safe"]);

        Assert.Equal(first, second);
        Assert.Equal(1, recommender.Calls);
    }

    [Fact]
    public async Task A_different_content_rating_ceiling_is_a_different_pool()
    {
        // The key carries the ceiling but no user id, so two people with the same ceiling share an
        // entry and somebody allowed more never inherits somebody else's narrower results.
        var recommender = new CountingRecommender();
        var service = Service(recommender);

        await service.GetAsync(1, ["safe"]);
        await service.GetAsync(1, ["safe", "suggestive"]);
        await service.GetAsync(1, ["safe"]);

        Assert.Equal(2, recommender.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_for_one_series_share_a_single_scan()
    {
        // Several tabs on the same series, or one page mounting twice in StrictMode, must not each
        // start their own pass over the index.
        var recommender = new CountingRecommender { Gate = new TaskCompletionSource() };
        var service = Service(recommender);

        var calls = Enumerable.Range(0, 5).Select(_ => service.GetAsync(1, ["safe"])).ToList();
        recommender.Gate.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(1, recommender.Calls);
        Assert.All(calls, c => Assert.Equal("77", c.Result.Single().ProviderId));
    }
}
