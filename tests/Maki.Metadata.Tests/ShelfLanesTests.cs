using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

public class ShelfLanesTests
{
    private static readonly ShelfLanes.Key Main = new(0, 1);
    private static readonly ShelfLanes.Key Romance = new(1, 2);
    private static readonly ShelfLanes.Key Other = new(1, 0);

    private sealed record Pick(long Id, ShelfLanes.Key? Key, int Rank = 5000);

    private static List<Pick> Page(int count, ShelfLanes.Key key, int firstId = 1) =>
        Enumerable.Range(firstId, count).Select(i => new Pick(i, key)).ToList();

    private static IEnumerable<(long, ShelfLanes.Key?, double)> Seeds(ShelfLanes.Key key, int count, int firstId, double weight = 1) =>
        Enumerable.Range(firstId, count).Select(i => ((long)i, (ShelfLanes.Key?)key, weight));

    private static List<Pick> Merge(
        List<Pick> page, IReadOnlyList<(List<Pick>, int)> queues, double[]? limits = null, params ShelfLanes.Key[] sides) =>
        ShelfLanes.Merge(
            page, (sides.Length == 0 ? [Romance] : sides).ToHashSet(), queues, p => p.Key, p => p.Rank, 10, limits);

    [Fact]
    public void A_side_taste_needs_three_seeds_and_the_minimum_share_and_the_largest_group_is_never_one()
    {
        var sides = ShelfLanes.Sides(
            Seeds(Main, 20, 1)
                .Concat(Seeds(Romance, 3, 100))
                .Concat(Seeds(Other, 2, 200))
                .Concat(Seeds(new ShelfLanes.Key(2, 0), 3, 300, weight: 0.1)),
            minShare: 0.05,
            maxSides: 3);

        var side = Assert.Single(sides);
        Assert.Equal(Romance, side.Key);
        Assert.Equal([100L, 101L, 102L], side.Ids);
    }

    [Fact]
    public void One_taste_has_no_side_and_unplaced_or_unweighted_seeds_count_for_nothing()
    {
        var seeds = Seeds(Main, 10, 1)
            .Append((50L, null, 1.0))
            .Concat(Seeds(Romance, 3, 100, weight: 0));

        Assert.Empty(ShelfLanes.Sides(seeds, 0.05, 3));
    }

    [Fact]
    public void At_most_the_heaviest_sides_are_kept()
    {
        var sides = ShelfLanes.Sides(
            Seeds(Main, 20, 1).Concat(Seeds(Other, 3, 100)).Concat(Seeds(Romance, 4, 200)),
            minShare: 0.05,
            maxSides: 1);

        Assert.Equal(Romance, Assert.Single(sides).Key);
    }

    [Fact]
    public void The_target_is_the_ratio_of_the_share_of_the_page_and_counts_what_the_page_holds()
    {
        var sides = ShelfLanes.Sides(Seeds(Main, 15, 1).Concat(Seeds(Romance, 5, 100)), 0.05, 3);
        var page = Page(38, Main).Concat(Page(2, Romance, 100)).ToList();

        var shortfall = Assert.Single(ShelfLanes.Shortfalls(page, sides, p => p.Key, 0.5, heldOnly: false));

        Assert.Equal(5, shortfall.Target);
        Assert.Equal(2, shortfall.Have);
        Assert.Empty(ShelfLanes.Shortfalls(page, sides, p => p.Key, 0.2, heldOnly: false));
        Assert.Empty(ShelfLanes.Shortfalls(Page(40, Main), sides, p => p.Key, 0.5, heldOnly: true));
    }

    [Fact]
    public void Inserts_fill_the_shortfall_below_the_top_ten_in_place_of_the_lowest_picks()
    {
        var page = Page(40, Main);
        var lane = Page(5, Romance, 100);

        var merged = Merge(page, [(lane, 3)]);

        Assert.Equal(40, merged.Count);
        Assert.Equal(page.Take(10), merged.Take(10));
        Assert.Equal([15, 25, 35], merged.Select((p, i) => (p, i)).Where(x => x.p.Key == Romance).Select(x => x.i));
        Assert.Equal([100L, 101L, 102L], merged.Where(p => p.Key == Romance).Select(p => p.Id));
        Assert.DoesNotContain(merged, p => p.Id is 38 or 39 or 40);
        Assert.Equal(page.Take(37), merged.Where(p => p.Key == Main));
    }

    [Fact]
    public void A_pick_of_a_side_taste_is_never_displaced()
    {
        var page = Page(37, Main).Concat(Page(3, Romance, 100)).ToList();

        var merged = Merge(page, [(Page(5, Romance, 200), 2)]);

        Assert.Contains(merged, p => p.Id == 100);
        Assert.Contains(merged, p => p.Id == 102);
        Assert.DoesNotContain(merged, p => p.Id is 36 or 37);
    }

    [Fact]
    public void An_insert_that_adds_a_fame_breach_waits_for_a_niche_one()
    {
        var page = Page(40, Main);
        var lane = new List<Pick> { new(100, Romance, Rank: 50), new(101, Romance, Rank: 6000) };
        double[] limits = [0, 0, 0];

        Assert.Contains(Merge(page, [(lane, 1)]), p => p.Id == 100);
        var capped = Merge(page, [(lane, 1)], limits);
        Assert.DoesNotContain(capped, p => p.Id == 100);
        Assert.Contains(capped, p => p.Id == 101);
    }

    [Fact]
    public void With_nothing_insertable_the_page_comes_back_as_it_was()
    {
        var page = Page(40, Main);
        double[] limits = [0, 0, 0];

        Assert.Equal(page, Merge(page, [([new Pick(100, Romance, Rank: 50)], 1)], limits));
        Assert.Equal(page, Merge(page, []));
        Assert.Equal(page.Take(10), Merge(page.Take(10).ToList(), [(Page(3, Romance, 100), 3)]));
    }
}
