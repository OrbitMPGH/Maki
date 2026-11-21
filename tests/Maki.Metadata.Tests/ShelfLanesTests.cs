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

    [Fact]
    public void A_refused_insert_leaves_a_protected_side_pick_at_the_bottom_where_it_was()
    {
        var page = Page(39, Main).Append(new Pick(100, Romance)).ToList();
        double[] limits = [0, 0, 0];

        var merged = Merge(page, [([new Pick(200, Romance, Rank: 50)], 1)], limits);

        Assert.Equal(page, merged);
    }

    [Fact]
    public void With_every_insert_refused_the_page_is_identical()
    {
        var page = Page(30, Main).Concat(Page(5, Romance, 100)).Concat(Page(5, Main, 31)).ToList();
        double[] limits = [0, 0, 0];
        var famous = new List<Pick> { new(200, Romance, Rank: 10), new(201, Romance, Rank: 20) };
        var famousOther = new List<Pick> { new(300, Other, Rank: 30) };

        var merged = Merge(page, [(famous, 2), (famousOther, 1)], limits, Romance, Other);

        Assert.Equal(page, merged);
    }

    [Fact]
    public void A_partly_refused_top_up_displaces_only_as_many_picks_as_it_inserts()
    {
        var page = Page(40, Main);
        double[] limits = [0, 0, 0];
        var lane = new List<Pick> { new(101, Romance, Rank: 6000), new(100, Romance, Rank: 50) };

        var merged = Merge(page, [(lane, 2)], limits);

        // Slots 17 and 32 are planned; the second has only the famous pick left, so it stays as it was.
        Assert.Equal([.. page.Take(17), new Pick(101, Romance, Rank: 6000), .. page.Skip(17).Take(22)], merged);
    }

    [Fact]
    public void Two_queues_take_turns_by_how_far_each_is_from_its_need()
    {
        var page = Page(40, Main);
        var romance = Page(3, Romance, 100);
        var other = Page(3, Other, 200);

        var merged = Merge(page, [(romance, 2), (other, 1)], null, Romance, Other);

        Assert.Equal(
            [(15, 100L), (25, 200L), (35, 101L)],
            merged.Select((p, i) => (i, p.Id)).Where(x => x.Id >= 100));
        Assert.Equal(page.Take(37), merged.Where(p => p.Key == Main));
    }

    [Fact]
    public void A_queue_shorter_than_its_need_inserts_what_it_has()
    {
        var page = Page(40, Main);

        var merged = Merge(page, [(Page(1, Romance, 100), 3)]);

        Assert.Equal([.. page.Take(15), new Pick(100, Romance), .. page.Skip(15).Take(24)], merged);
    }

    [Fact]
    public void Picks_past_the_page_keep_the_whole_order_with_the_displaced_first_and_nothing_twice()
    {
        var whole = Page(200, Main);
        var page = whole.Take(40).ToList();
        // 150 is a lane pick that the whole-shelf ranking also had, at rank 150.
        var lane = new List<Pick> { new(150, Romance), new(300, Romance) };

        var merged = Merge(page, [(lane, 2)]);
        var pool = ShelfLanes.Assemble(merged, whole, 40, p => p.Id, 200);

        Assert.Equal(200, pool.Count);
        Assert.Equal(merged, pool.Take(40));
        Assert.Equal([39L, 40L], pool.Skip(40).Take(2).Select(p => p.Id));
        Assert.Equal(
            whole.Skip(40).Where(p => p.Id != 150).Take(158).Select(p => p.Id),
            pool.Skip(42).Select(p => p.Id));
        Assert.Equal(pool.Count, pool.Select(p => p.Id).Distinct().Count());
        Assert.Single(pool, p => p.Id == 150);
    }

    [Fact]
    public void A_repeated_seed_counts_once()
    {
        var seeds = Seeds(Main, 20, 1).Concat(Enumerable.Repeat((100L, (ShelfLanes.Key?)Romance, 1.0), 3));

        Assert.Empty(ShelfLanes.Sides(seeds, 0.05, 3));
    }

    [Fact]
    public void Midpoint_targets_round_to_even()
    {
        // 1.5 and 2.5 picks both target 2.
        var page = Page(32, Main);
        var oneAndHalf = new ShelfLanes.Side(Romance, [100, 101, 102], 6, 64);
        var twoAndHalf = new ShelfLanes.Side(Other, [200, 201, 202], 10, 64);

        var shortfalls = ShelfLanes.Shortfalls(page, [oneAndHalf, twoAndHalf], p => p.Key, 0.5, heldOnly: false);

        Assert.Equal([2, 2], shortfalls.Select(s => s.Target));
    }
}
