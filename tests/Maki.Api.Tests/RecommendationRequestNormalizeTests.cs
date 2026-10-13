using Maki.Api.Services;
using Xunit;

namespace Maki.Api.Tests;

public class RecommendationRequestNormalizeTests
{
    [Fact]
    public void ABodyBoundRequestIsClampedBeforeItKeysAPool()
    {
        var request = new RecommendationRequest(
            SeedIds: Enumerable.Range(1, 5000).Select(x => (long)x).Append(1).ToList(),
            Obscurity: 40, Diversity: -3, Page: int.MaxValue);

        var normalized = RecommendationService.Normalize(request);

        Assert.Equal(RecommendationService.MaxSeeds, normalized.SeedIds!.Count);
        Assert.Equal(1, normalized.Obscurity);
        Assert.Equal(0, normalized.Diversity);
        Assert.Equal(RecommendationService.MaxPage, normalized.Page);
    }

    [Fact]
    public void AnOrdinaryRequestIsLeftAlone()
    {
        var request = new RecommendationRequest(SeedIds: [3, 1, 2], Obscurity: -0.5, Diversity: 0.25, Page: 2);

        var normalized = RecommendationService.Normalize(request);

        Assert.Equal([3L, 1L, 2L], normalized.SeedIds);
        Assert.Equal(-0.5, normalized.Obscurity);
        Assert.Equal(0.25, normalized.Diversity);
        Assert.Equal(2, normalized.Page);
    }
}
