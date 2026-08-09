using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

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
    public void FromQuantizedBlob_RoundTrips()
    {
        var original = new[] { -0.75f, -0.1f, 0f, 0.4f, 1f };
        var packed = new sbyte[original.Length];
        var scale = EmbeddingMath.Quantize(original, packed);
        var restored = EmbeddingMath.FromQuantizedBlob(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(packed.AsSpan()).ToArray(), scale);

        Assert.NotNull(restored);
        Assert.Equal(original.Length, restored!.Length);
        for (var i = 0; i < original.Length; i++)
        {
            Assert.InRange(MathF.Abs(original[i] - restored[i]), 0f, scale);
        }
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

    [Fact]
    public void SelectDiverse_ZeroLambda_IsThePlainRelevanceOrder()
    {
        // The default has to be inert, or turning MMR on would silently reorder every existing
        // user's recommendations.
        var relevance = new[] { 0.2, 0.9, 0.5 };

        var picked = EmbeddingMath.SelectDiverse(relevance, (_, _) => 1.0, take: 3, lambda: 0);

        Assert.Equal([1, 2, 0], picked);
    }

    [Fact]
    public void SelectDiverse_DemotesNearDuplicatesOfWhatIsAlreadyPicked()
    {
        // 0 and 1 are near-identical; 2 is slightly less relevant than 1 but unrelated to both.
        var relevance = new[] { 1.0, 0.9, 0.8 };
        double similarity(int a, int b) => (a, b) switch
        {
            (0, 1) or (1, 0) => 0.98,
            _ => 0.05,
        };

        Assert.Equal([0, 1, 2], EmbeddingMath.SelectDiverse(relevance, similarity, 3, lambda: 0));
        Assert.Equal([0, 2, 1], EmbeddingMath.SelectDiverse(relevance, similarity, 3, lambda: 0.5));
    }

    [Fact]
    public void SelectDiverse_HonoursTake_AndHandlesEmptyPools()
    {
        Assert.Equal([0], EmbeddingMath.SelectDiverse([1.0, 0.5], (_, _) => 0, take: 1, lambda: 0.5));
        Assert.Equal([0, 1], EmbeddingMath.SelectDiverse([1.0, 0.5], (_, _) => 0, take: 9, lambda: 0.5));
        Assert.Empty(EmbeddingMath.SelectDiverse([], (_, _) => 0, take: 5, lambda: 0.5));
        Assert.Empty(EmbeddingMath.SelectDiverse([1.0], (_, _) => 0, take: 0, lambda: 0.5));
    }

    [Fact]
    public void SelectDiverse_FullLambda_StillReturnsEveryRequestedPick()
    {
        // λ=1 ignores relevance after the first pick; it must not stall or drop candidates.
        var picked = EmbeddingMath.SelectDiverse([1.0, 0.9, 0.8], (_, _) => 0.5, take: 3, lambda: 1);

        Assert.Equal(3, picked.Count);
        Assert.Equal([0, 1, 2], picked.Order());
    }
}
