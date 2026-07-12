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
        var strong = EmbeddingMath.HybridScore(0.9, 0, 0, false, 70, w);
        var weak = EmbeddingMath.HybridScore(0.4, 0, 0, false, 70, w);
        Assert.True(strong > weak);
    }

    [Fact]
    public void HybridScore_StructuredSignalsAddOnTop()
    {
        var w = new EmbeddingMath.Weights();
        var bare = EmbeddingMath.HybridScore(0.6, 0, 0, false, 50, w);
        var withGenre = EmbeddingMath.HybridScore(0.6, 1.0, 0, false, 50, w);
        var withAuthor = EmbeddingMath.HybridScore(0.6, 0, 0, true, 50, w);
        Assert.True(withGenre > bare);
        Assert.True(withAuthor > bare);
    }
}
