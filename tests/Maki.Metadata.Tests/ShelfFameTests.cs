using Maki.Metadata.Embedding;

namespace Maki.Metadata.Tests;

public class ShelfFameTests
{
    private static readonly (int Rank, double Weight)[] NicheShelf =
        [.. Enumerable.Range(0, 40).Select(i => (i < 4 ? 50 : 5_000, 1.0))];

    private static readonly (int Rank, double Weight)[] MixedShelf =
        [.. Enumerable.Range(0, 40).Select(i => (i < 4 ? 50 : i < 10 ? 200 : i < 20 ? 800 : 5_000, 1.0))];

    [Fact]
    public void Limits_FollowTheShelfsShareInsideEachThreshold()
    {
        var limits = ShelfFame.Limits(MixedShelf, ratio: 1, pseudoSeeds: 0);

        Assert.Equal([0.1, 0.25, 0.5], limits.Select(l => Math.Round(l, 6)));
    }

    [Fact]
    public void Limits_BarelyConstrainASmallShelf()
    {
        (int, double)[] shelf = [(5_000, 1.0), (8_000, 1.0)];

        Assert.True(ShelfFame.Limits(shelf, ratio: 1, pseudoSeeds: 10)[0] > 0.8);
    }

    [Fact]
    public void Defer_PushesFamousPicksBackAndKeepsEveryOtherPickInOrder()
    {
        // Ten famous picks ahead of thirty obscure ones, against a shelf that is one tenth famous.
        var ranked = Enumerable.Range(0, 40).Select(i => (Id: i, Rank: i < 10 ? 10 : 20_000)).ToList();
        var limits = ShelfFame.Limits(NicheShelf, ratio: 1, pseudoSeeds: 0);

        var result = ShelfFame.Defer(ranked, p => p.Rank, limits, 20);
        var famous = result.Where(p => p.Rank == 10).Select(p => p.Id).ToList();
        var obscure = result.Where(p => p.Rank != 10).Select(p => p.Id).ToList();

        Assert.Equal(20, result.Count);
        Assert.Equal(0, result[0].Id);
        Assert.Equal([0, 1], famous);
        Assert.Equal(obscure.Order(), obscure);
    }

    [Fact]
    public void Defer_DropsNothingWhenTheListIsShorterThanTheTake()
    {
        var ranked = Enumerable.Range(0, 8).Select(i => (Id: i, Rank: 10)).ToList();
        var limits = ShelfFame.Limits(NicheShelf, ratio: 1, pseudoSeeds: 0);

        var result = ShelfFame.Defer(ranked, p => p.Rank, limits, 20);

        Assert.Equal(ranked.Select(p => p.Id).Order(), result.Select(p => p.Id).Order());
    }

    [Fact]
    public void Defer_LeavesAListAloneWhenItIsNoMoreFamousThanTheShelf()
    {
        var ranked = Enumerable.Range(0, 40).Select(i => (Id: i, Rank: i % 10 == 0 ? 50 : 5_000)).ToList();
        var limits = ShelfFame.Limits(NicheShelf, ratio: 1, pseudoSeeds: 0);

        Assert.Equal(ranked, ShelfFame.Defer(ranked, p => p.Rank, limits, 40));
    }
}
