using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Tests;

/// <summary>
/// <c>RecommendationService.Spread</c>, the pass that stops one same-work component owning a run of
/// the similar pool. It is a re-ordering and nothing else: what the scorer picked has to survive it
/// intact, or the measurement behind <c>RecommenderTuning.MaxPerFranchise</c> (suppressing franchise
/// members costs relevance) would be quietly overridden by a display fix.
/// </summary>
public class RecommendationSpreadTests
{
    private static MangaBakaRecommendation Pick(int id, int? franchise = null) =>
        new($"{id}", $"Pick {id}", null, null, null, SeriesStatus.Completed, 80, null,
            [], [], false, null, null, FranchiseId: franchise);

    [Fact]
    public void A_pool_with_no_franchises_is_left_exactly_as_it_was()
    {
        var ranked = Enumerable.Range(1, 20).Select(i => Pick(i)).ToList();

        Assert.Equal(ranked, RecommendationService.Spread(ranked));
    }

    [Fact]
    public void Nothing_is_dropped_or_duplicated()
    {
        List<MangaBakaRecommendation> ranked =
        [
            .. Enumerable.Range(1, 10).Select(i => Pick(i, franchise: 3)),
            .. Enumerable.Range(11, 30).Select(i => Pick(i)),
        ];

        var spread = RecommendationService.Spread(ranked);

        Assert.Equal(ranked.Count, spread.Count);
        Assert.Equal(
            ranked.Select(p => p.ProviderId).OrderBy(id => id),
            spread.Select(p => p.ProviderId).OrderBy(id => id));
    }

    [Fact]
    public void Members_of_one_franchise_end_up_at_least_eight_apart()
    {
        List<MangaBakaRecommendation> ranked =
        [
            .. Enumerable.Range(1, 5).Select(i => Pick(i, franchise: 3)),
            .. Enumerable.Range(11, 40).Select(i => Pick(i)),
        ];

        var spread = RecommendationService.Spread(ranked);

        var positions = spread
            .Select((pick, at) => (pick.FranchiseId, At: at))
            .Where(x => x.FranchiseId == 3)
            .Select(x => x.At)
            .ToList();

        Assert.Equal(5, positions.Count);
        Assert.All(positions.Zip(positions.Skip(1)), pair => Assert.True(pair.Second - pair.First >= 8));
    }

    [Fact]
    public void The_order_inside_a_franchise_is_the_order_the_scorer_gave()
    {
        List<MangaBakaRecommendation> ranked =
        [
            .. Enumerable.Range(1, 4).Select(i => Pick(i, franchise: 3)),
            .. Enumerable.Range(11, 40).Select(i => Pick(i)),
        ];

        var spread = RecommendationService.Spread(ranked);

        Assert.Equal(
            ["1", "2", "3", "4"],
            spread.Where(p => p.FranchiseId == 3).Select(p => p.ProviderId));
    }

    [Fact]
    public void A_tail_of_one_franchise_does_not_stall_the_pass()
    {
        // Nothing left to interleave with, so the spacing cannot be honoured. The pass has to hand
        // the rest back rather than loop, and it must still hand back all of it.
        var ranked = Enumerable.Range(1, 12).Select(i => Pick(i, franchise: 3)).ToList();

        var spread = RecommendationService.Spread(ranked);

        Assert.Equal(ranked, spread);
    }

    [Fact]
    public void Two_franchises_interleave_rather_than_queue()
    {
        List<MangaBakaRecommendation> ranked =
        [
            .. Enumerable.Range(1, 3).Select(i => Pick(i, franchise: 3)),
            .. Enumerable.Range(21, 3).Select(i => Pick(i, franchise: 4)),
            .. Enumerable.Range(31, 20).Select(i => Pick(i)),
        ];

        var spread = RecommendationService.Spread(ranked);

        // The best pick of each franchise still leads, back to back: only a franchise's second
        // member is held back, so neither one has to wait behind the other.
        Assert.Equal("1", spread[0].ProviderId);
        Assert.Equal("21", spread[1].ProviderId);
        Assert.All(
            spread.Take(9).Where(p => p.FranchiseId is not null).GroupBy(p => p.FranchiseId),
            franchise => Assert.True(franchise.Count() <= 2));
    }
}
