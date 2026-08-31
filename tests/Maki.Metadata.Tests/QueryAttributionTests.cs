using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The per-query standardization behind <see cref="QueryAttribution"/>, and the premise it rests on.
/// Tested against <see cref="SemanticRecommender.MeasureQueries"/> directly rather than through
/// <c>GetSimilarAsync</c>: the claim is about the shape of each query channel's cosine distribution,
/// which end to end is only visible as a label changing on some rows and not others.
/// </summary>
public class QueryAttributionTests
{
    private const int Dim = 32;

    [Fact]
    public void MeasureQueries_MeasuresSurvivorsOnly()
    {
        // Rejected rows are negative infinity in every channel. Folding them in would drag every
        // mean to negative infinity and make the whole thing NaN, so this is load-bearing rather
        // than defensive.
        var cosines = new[]
        {
            new[] { 0.2f, float.NegativeInfinity, 0.4f, 0.6f, float.NegativeInfinity },
        };

        var scale = SemanticRecommender.MeasureQueries(cosines)[0];

        Assert.Equal(0.4, scale.Mean, 6);
        Assert.Equal(Math.Sqrt(((0.2 - 0.4) * (0.2 - 0.4)) + ((0.6 - 0.4) * (0.6 - 0.4))) / Math.Sqrt(3),
            scale.Deviation, 6);
    }

    [Fact]
    public void MeasureQueries_ReportsNoDeviation_ForAChannelThatSaysNothing()
    {
        // A channel with no spread ranks every row identically, so it distinguishes nothing and must
        // not become a division. The scoring loop reads a zero deviation as "every row sits at the
        // mean" and credits the channel zero rather than infinity.
        var flat = SemanticRecommender.MeasureQueries([[0.5f, 0.5f, 0.5f]])[0];
        var empty = SemanticRecommender.MeasureQueries(
            [[float.NegativeInfinity, float.NegativeInfinity]])[0];

        Assert.Equal(0.5, flat.Mean, 6);
        Assert.Equal(0, flat.Deviation);
        Assert.Equal(0, empty.Mean);
        Assert.Equal(0, empty.Deviation);
    }

    [Fact]
    public void TheCentroidChannelOutscoresEverySeedChannelOnAverage_WhichIsTheWholeProblem()
    {
        // Not a bug report, a pinned property, and the premise QueryAttribution.Standardized exists
        // to correct. Seeds in one cone (which is what an anisotropic embedding space gives you)
        // make the centroid's normalization divide by less than it summed, so its cosines land above
        // every seed's by a roughly constant factor that is an artifact of the arithmetic rather
        // than evidence about any candidate. Under a bare maximum that factor decides the generic
        // middle of the catalogue, which is most of it.
        var rng = new Random(7);
        var seeds = new[] { Cone(rng), Cone(rng), Cone(rng), Cone(rng) };
        var candidates = new float[400][];
        for (var i = 0; i < candidates.Length; i++)
        {
            candidates[i] = Cone(rng);
        }

        var centroid = EmbeddingMath.WeightedMean([.. seeds.Select(s => (s, 1.0))])!;
        var queries = new[] { centroid }.Concat(seeds).ToArray();

        var cosines = new float[queries.Length][];
        for (var q = 0; q < queries.Length; q++)
        {
            cosines[q] = new float[candidates.Length];
            for (var i = 0; i < candidates.Length; i++)
            {
                cosines[q][i] = EmbeddingMath.Cosine(queries[q], candidates[i]);
            }
        }

        var scales = SemanticRecommender.MeasureQueries(cosines);

        Assert.All(scales.Skip(1), seed => Assert.True(
            scales[0].Mean > seed.Mean,
            $"centroid mean {scales[0].Mean:F3} should sit above seed mean {seed.Mean:F3}"));

        // And the offset is the size the arithmetic predicts, not a rounding difference: about
        // 1/sqrt(mean pairwise seed cosine). Asserted as a band because the cone is random.
        var ratio = scales[0].Mean / scales.Skip(1).Average(s => s.Mean);
        Assert.InRange(ratio, 1.05, 1.60);
    }

    [Fact]
    public void AttributionCutoff_PassesTheMarginThrough_WhenItIsAbsolute()
    {
        var tuning = RecommenderTuning.Default with
        {
            AttributionScale = AttributionScale.Absolute, AttributionMargin = 1.25,
        };

        Assert.Equal(1.25, SemanticRecommender.AttributionCutoff([0.1, 5.0, -2.0], tuning));
        // Including when the pool is empty: an absolute bar does not depend on there being one.
        Assert.Equal(1.25, SemanticRecommender.AttributionCutoff([], tuning));
    }

    [Fact]
    public void AttributionCutoff_ReadsTheMarginAsStandardDeviations_WhenItIsPoolRelative()
    {
        var pool = new[] { 0.0, 1.0, 2.0, 3.0, 4.0 };
        var tuning = RecommenderTuning.Default with { AttributionScale = AttributionScale.PoolRelative };

        // mean 2, population sd sqrt(2)
        Assert.Equal(2.0, SemanticRecommender.AttributionCutoff(pool, tuning with { AttributionMargin = 0 }), 6);
        Assert.Equal(2 + Math.Sqrt(2), SemanticRecommender.AttributionCutoff(
            pool, tuning with { AttributionMargin = 1 }), 6);
    }

    [Fact]
    public void AttributionCutoff_NeverDropsBelowZero_AndNamesNobodyWhenNothingStandsOut()
    {
        var tuning = RecommenderTuning.Default with { AttributionScale = AttributionScale.PoolRelative };

        // A pool that is mostly negative would put mean + k*sd below zero, and a cutoff below zero
        // would name rows the centroid explains better than any seed. The floor is what stops the
        // relative rule reintroducing the thing the whole margin exists to prevent.
        Assert.Equal(0, SemanticRecommender.AttributionCutoff(
            [-5.0, -4.0, -3.0, -2.0], tuning with { AttributionMargin = 0 }));

        // No spread at all: nothing is distinctive relative to anything, so nobody is named. The
        // opposite convention would name the entire page on a pool where every row is identical.
        Assert.Equal(double.PositiveInfinity, SemanticRecommender.AttributionCutoff(
            [1.5, 1.5, 1.5], tuning with { AttributionMargin = 0 }));
        Assert.Equal(double.PositiveInfinity, SemanticRecommender.AttributionCutoff([], tuning));
    }

    /// <summary>
    /// A unit vector in one shared cone: a fixed direction every vector leans on, plus per-vector
    /// noise. Stands in for the shared "this is a manga description" component real sentence
    /// embeddings carry, which is what keeps the mean pairwise seed cosine well above zero.
    /// </summary>
    private static float[] Cone(Random rng)
    {
        var v = new float[Dim];
        for (var i = 0; i < Dim; i++)
        {
            v[i] = (float)(rng.NextDouble() - 0.5);
        }

        v[0] += 1.4f;
        v[1] += 1.0f;
        EmbeddingMath.NormalizeInPlace(v);
        return v;
    }
}
