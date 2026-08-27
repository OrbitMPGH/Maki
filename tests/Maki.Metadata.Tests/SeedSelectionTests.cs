using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// Which seeds get their own query when a library has more of them than the recommender will pay
/// for. Tested directly rather than through <c>GetSimilarAsync</c>: the centroid query is built from
/// every seed regardless, so end to end the strategies mostly agree and the thing being chosen is
/// invisible. What each one actually picks is the claim.
/// </summary>
public class SeedSelectionTests
{
    private const int Dim = 8;

    /// <summary>
    /// Eight titles that all sit together (what the library is mostly about) plus two outliers, with
    /// the outliers holding the LOW ids so they win the weight-then-id ordering every strategy starts
    /// from. That ordering is what makes the difference between the strategies visible at all.
    /// </summary>
    private static Dictionary<long, float[]> Library()
    {
        var seeds = new Dictionary<long, float[]>
        {
            [1] = Axis(5),
            [2] = Axis(6),
        };

        for (var i = 0; i < 8; i++)
        {
            seeds[10 + i] = Nudge(Axis(0), 1 + (i % 3), 0.05f + (i * 0.01f));
        }

        return seeds;
    }

    [Fact]
    public void Farthest_SpendsBothQueriesOnTheOutliers_AndNoneOnWhatTheLibraryIsAbout()
    {
        // Not a bug report, a pinned property. Greedy farthest-point sampling walks the OUTSIDE of
        // the seed set by construction: it starts at the first seed and every step after maximizes
        // distance from what is already picked, so with two queries to spend on a library that is
        // eight romance titles and two oddities, both go to the oddities. The centroid query is what
        // covers the mass, which is why this is survivable — but it is the reason `Medoid` exists.
        var picked = SemanticRecommender.PickRepresentativeSeeds(
            Library(), null, new RecommenderTuning { MaxSeedQueries = 2 });

        Assert.Equal([1L, 2L], picked);
    }

    [Fact]
    public void Medoid_PicksTheClusterTheLibraryActuallyIs()
    {
        // Same library, same budget, and one of the two queries now lands inside the eight-title
        // cluster instead of on a second oddity.
        var picked = SemanticRecommender.PickRepresentativeSeeds(
            Library(), null, new RecommenderTuning { MaxSeedQueries = 2, SeedSelection = SeedSelection.Medoid });

        Assert.Equal(2, picked.Count);
        Assert.Contains(picked, id => id >= 10);
    }

    [Fact]
    public void Weight_SpendsEveryQueryOnNearDuplicates_WhichIsWhyFarthestExists()
    {
        // The obvious strategy, and the one the farthest-point walk was written to avoid. Weight the
        // eight clustered titles above the outliers and taking the top N by weight returns eight
        // near-copies: eight queries, one direction, seven of them buying nothing.
        var weights = Library().Keys.ToDictionary(id => id, id => id >= 10 ? 1.8 : 0.4);

        var picked = SemanticRecommender.PickRepresentativeSeeds(
            Library(), weights, new RecommenderTuning { MaxSeedQueries = 4, SeedSelection = SeedSelection.Weight });

        Assert.All(picked, id => Assert.True(id >= 10));
    }

    [Fact]
    public void WeightedFarthest_LetsTasteSteerTheWholeWalk_NotOnlyItsStart()
    {
        // Plain farthest-point sampling reads the weights once, to choose where to start. This one
        // multiplies every step by the candidate's weight, so an outlier nobody liked stops being
        // chosen purely for being far away.
        //
        // Seed 2 is orthogonal to everything and barely touched; seeds 10+ sit near seed 1 and were
        // read to the end. Distance alone picks seed 2; distance times taste does not.
        var seeds = new Dictionary<long, float[]> { [1] = Axis(0), [2] = Axis(6) };
        for (var i = 0; i < 6; i++)
        {
            seeds[10 + i] = Nudge(Axis(0), 1 + (i % 3), 0.5f + (i * 0.05f));
        }

        var weights = seeds.Keys.ToDictionary(id => id, id => id == 2 ? 0.05 : 1.8);

        var plain = SemanticRecommender.PickRepresentativeSeeds(
            seeds, weights, new RecommenderTuning { MaxSeedQueries = 2 });
        var weighted = SemanticRecommender.PickRepresentativeSeeds(
            seeds, weights,
            new RecommenderTuning { MaxSeedQueries = 2, SeedSelection = SeedSelection.WeightedFarthest });

        Assert.Contains(2L, plain);
        Assert.DoesNotContain(2L, weighted);
    }

    [Theory]
    [InlineData(SeedSelection.Farthest)]
    [InlineData(SeedSelection.Weight)]
    [InlineData(SeedSelection.Medoid)]
    [InlineData(SeedSelection.WeightedFarthest)]
    public void EverySeedIsQueriedBelowTheCap_SoTheStrategyCannotMatterThere(SeedSelection selection)
    {
        // Which is why this only moves anything on a whole-library request, and why the "More like
        // this" rail and a small seeded Discover are untouched by any of it.
        var seeds = Library();
        var picked = SemanticRecommender.PickRepresentativeSeeds(
            seeds, null, new RecommenderTuning { MaxSeedQueries = 32, SeedSelection = selection });

        Assert.Equal(seeds.Count, picked.Count);
        Assert.Equal(seeds.Keys.Order(), picked.Order());
    }

    [Theory]
    [InlineData(SeedSelection.Farthest)]
    [InlineData(SeedSelection.Medoid)]
    [InlineData(SeedSelection.WeightedFarthest)]
    public void SelectionIsDeterministic_BecauseAPoolCacheKeyPromisesItIs(SeedSelection selection)
    {
        // RecommendationService caches a pool for 12 hours against a key that says nothing about
        // which seeds were queried. A strategy that answered differently on two identical requests
        // would make that key a lie, which is why the k-means seeding is the farthest-point walk
        // rather than a random draw.
        var tuning = new RecommenderTuning { MaxSeedQueries = 3, SeedSelection = selection };

        Assert.Equal(
            SemanticRecommender.PickRepresentativeSeeds(Library(), null, tuning),
            SemanticRecommender.PickRepresentativeSeeds(Library(), null, tuning));
    }

    [Fact]
    public void Medoid_StillReturnsAFullBudget_WhenAClusterComesOutEmpty()
    {
        // k-means can leave a centre with nothing assigned to it. Returning fewer seeds than asked
        // for would quietly shrink the scan the caller is paying for, so empty clusters are backfilled
        // by weight rather than dropped.
        var seeds = new Dictionary<long, float[]>
        {
            [1] = Axis(0),
            [2] = Nudge(Axis(0), 1, 0.01f),
            [3] = Nudge(Axis(0), 1, 0.02f),
            [4] = Nudge(Axis(0), 1, 0.03f),
            [5] = Nudge(Axis(0), 1, 0.04f),
        };

        var picked = SemanticRecommender.PickRepresentativeSeeds(
            seeds, null, new RecommenderTuning { MaxSeedQueries = 4, SeedSelection = SeedSelection.Medoid });

        Assert.Equal(4, picked.Count);
        Assert.Equal(4, picked.Distinct().Count());
    }

    private static float[] Axis(int i)
    {
        var v = new float[Dim];
        v[i] = 1f;
        return v;
    }

    private static float[] Nudge(float[] v, int axis, float amount)
    {
        var copy = (float[])v.Clone();
        copy[axis] += amount;
        EmbeddingMath.NormalizeInPlace(copy);
        return copy;
    }
}
