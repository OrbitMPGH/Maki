using Mangarr.Metadata.Embedding;
using Xunit;

namespace Mangarr.Metadata.Tests;

public class EmbeddingMathTests
{
    [Fact]
    public void NormalizeInPlace_MakesUnitLength()
    {
        var v = new[] { 3f, 4f };
        EmbeddingMath.NormalizeInPlace(v);
        Assert.Equal(0.6f, v[0], 3);
        Assert.Equal(0.8f, v[1], 3);
        Assert.Equal(1f, MathF.Sqrt((v[0] * v[0]) + (v[1] * v[1])), 3);
    }

    [Fact]
    public void NormalizeInPlace_ZeroVector_IsUnchanged()
    {
        var v = new[] { 0f, 0f, 0f };
        EmbeddingMath.NormalizeInPlace(v);
        Assert.Equal([0f, 0f, 0f], v);
    }

    [Fact]
    public void Cosine_IdenticalUnitVectors_IsOne()
    {
        var a = new[] { 0.6f, 0.8f };
        Assert.Equal(1f, EmbeddingMath.Cosine(a, a), 3);
    }

    [Fact]
    public void Cosine_Orthogonal_IsZero()
    {
        Assert.Equal(0f, EmbeddingMath.Cosine([1f, 0f], [0f, 1f]), 3);
    }

    [Fact]
    public void Cosine_MismatchedLengths_IsZero() =>
        Assert.Equal(0f, EmbeddingMath.Cosine([1f, 0f], [1f, 0f, 0f]));

    [Fact]
    public void Mean_ReturnsRenormalizedAverageDirection()
    {
        // Two unit vectors 90° apart average to the 45° direction, re-normalized.
        var mean = EmbeddingMath.Mean([[1f, 0f], [0f, 1f]]);
        Assert.NotNull(mean);
        var inv = 1f / MathF.Sqrt(2f);
        Assert.Equal(inv, mean![0], 3);
        Assert.Equal(inv, mean[1], 3);
    }

    [Fact]
    public void Mean_Empty_IsNull() => Assert.Null(EmbeddingMath.Mean([]));

    [Fact]
    public void MostSimilar_PicksHighestCosineSeed()
    {
        var candidate = new[] { 0.9f, 0.1f };
        // Seed 1 points almost the same way as the candidate; seed 0 is orthogonal.
        var seeds = new List<float[]> { new[] { 0f, 1f }, new[] { 1f, 0f } };
        Assert.Equal(1, EmbeddingMath.MostSimilar(candidate, seeds));
    }

    [Fact]
    public void MostSimilar_NoSeeds_IsNegativeOne() =>
        Assert.Equal(-1, EmbeddingMath.MostSimilar([1f, 0f], []));

    [Fact]
    public void Blob_RoundTrips()
    {
        var v = new[] { 0.1f, -0.2f, 3.14159f, 0f, 42f };
        var back = EmbeddingMath.FromBlob(EmbeddingMath.ToBlob(v));
        Assert.Equal(v, back);
    }

    [Fact]
    public void FromBlob_BadLength_IsNull()
    {
        Assert.Null(EmbeddingMath.FromBlob(new byte[] { 1, 2, 3 })); // not a multiple of 4
        Assert.Null(EmbeddingMath.FromBlob([]));
    }

    [Fact]
    public void HybridScore_SemanticDominatesWhenStructuredEqual()
    {
        var w = new EmbeddingMath.Weights();
        var strong = EmbeddingMath.HybridScore(0.9, 0, 0, false, 70, 0, 0.5, w);
        var weak = EmbeddingMath.HybridScore(0.4, 0, 0, false, 70, 0, 0.5, w);
        Assert.True(strong > weak);
    }

    [Fact]
    public void HybridScore_StructuredSignalsAddOnTop()
    {
        var w = new EmbeddingMath.Weights();
        var bare = EmbeddingMath.HybridScore(0.6, 0, 0, false, 50, 0, 0.5, w);
        var withGenre = EmbeddingMath.HybridScore(0.6, 1.0, 0, false, 50, 0, 0.5, w);
        var withAuthor = EmbeddingMath.HybridScore(0.6, 0, 0, true, 50, 0, 0.5, w);
        Assert.True(withGenre > bare);
        Assert.True(withAuthor > bare);
    }

    [Fact]
    public void HybridScore_ObscurityDial_BiasesByPopularity()
    {
        var w = new EmbeddingMath.Weights();
        // An obscure title (percentile 0.9) vs a mainstream one (0.1), all else equal.
        double obscure(double slider) => EmbeddingMath.HybridScore(0.6, 0, 0, false, 50, slider, 0.9, w);
        double mainstream(double slider) => EmbeddingMath.HybridScore(0.6, 0, 0, false, 50, slider, 0.1, w);

        // Slider = 0: no effect, both equal.
        Assert.Equal(obscure(0), mainstream(0), 6);
        // Slider = +1 (hidden gems): the obscure title scores higher.
        Assert.True(obscure(1) > mainstream(1));
        // Slider = -1 (mainstream): the popular title scores higher.
        Assert.True(mainstream(-1) > obscure(-1));
    }
}
