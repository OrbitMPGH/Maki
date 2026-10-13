using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

public class FuseByRankTests
{
    /// <summary>The full sort the bounded selection replaced, with ties broken on the lower row.</summary>
    private static List<int> Reference(float[][] cosines, int rowCount, int poolPerQuery)
    {
        var survivors = Enumerable.Range(0, rowCount)
            .Where(row => !float.IsNegativeInfinity(cosines[0][row])).ToList();
        var pooled = new List<int>();
        var seen = new HashSet<int>();
        foreach (var channel in cosines)
        {
            foreach (var row in survivors.OrderByDescending(r => channel[r]).ThenBy(r => r).Take(poolPerQuery))
            {
                if (seen.Add(row))
                {
                    pooled.Add(row);
                }
            }
        }

        return pooled;
    }

    [Theory]
    [InlineData(5000, 1, 200)]
    [InlineData(5000, 12, 200)]
    [InlineData(300, 4, 2000)]
    [InlineData(0, 3, 200)]
    public void Matches_a_full_sort_of_every_channel(int rowCount, int channels, int poolPerQuery)
    {
        var random = new Random(7);
        var cosines = Enumerable.Range(0, channels).Select(_ => Enumerable.Range(0, rowCount)
            // Coarse values so ties are common and the tie-break is exercised.
            .Select(_ => MathF.Round((float)random.NextDouble(), 2)).ToArray()).ToArray();
        for (var row = 0; row < rowCount; row += 7)
        {
            cosines[0][row] = float.NegativeInfinity;
        }

        Assert.Equal(Reference(cosines, rowCount, poolPerQuery),
            SemanticRecommender.FuseByRank(cosines, rowCount, poolPerQuery));
    }
}
