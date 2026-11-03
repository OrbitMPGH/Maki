using Maki.Metadata.RecoGraph;

namespace Maki.Metadata.Tests;

/// <summary>
/// The shipped graphs are small enough for <c>ushort</c> neighbours, so the cache tests never reach
/// the <c>int</c> path a graph past 65,536 nodes switches to. These build chains either side of it.
/// </summary>
public class PairGraphBuilderTests
{
    [Theory]
    [InlineData(65_536)]
    [InlineData(70_001)]
    public void EveryNodeFindsItsNeighboursEitherSideOfTheNarrowLimit(int nodes)
    {
        static long Id(int i) => 5_000_000L - 3L * i;
        var pairs = Enumerable.Range(0, nodes - 1).Select(i => (Id(i), Id(i + 1), (float)(i % 100 + 1)));

        var graph = PairGraphBuilder.Build(() => pairs, null);

        Assert.Equal(nodes, graph.Count);
        Assert.Equal(2 * (nodes - 1), graph.EdgeCount);
        foreach (var i in new[] { 0, 1, 65_534, 65_535, nodes - 2, nodes - 1 }.Distinct())
        {
            Assert.True(graph.TryGetNode(Id(i), out var node));
            var edges = graph.EdgesAt(node);
            var neighbours = new Dictionary<long, float>();
            for (var e = 0; e < edges.Length; e++)
            {
                neighbours[graph.IdAt(edges.Neighbour(e))] = edges.Weight(e);
            }

            var expected = new Dictionary<long, float>();
            if (i > 0)
            {
                expected[Id(i - 1)] = (i - 1) % 100 + 1;
            }

            if (i < nodes - 1)
            {
                expected[Id(i + 1)] = i % 100 + 1;
            }

            Assert.Equal(expected, neighbours);
        }

        Assert.False(graph.TryGetNode(Id(nodes), out _));
    }
}
