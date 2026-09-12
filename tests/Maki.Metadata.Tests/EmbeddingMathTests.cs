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

    /// <summary>
    /// Widths chosen around the vector lane boundary: 512 is the narrowest that packs at all, 768
    /// is what ships, and 520 and 1025 land mid-lane and odd so the scalar tail and the odd-element
    /// case are both exercised rather than assumed.
    /// </summary>
    [Theory]
    [InlineData(512)]
    [InlineData(520)]
    [InlineData(768)]
    [InlineData(1025)]
    public void PackQuantized_RoundTripsEveryLevel(int dimensions)
    {
        var rng = new Random(20260912);
        var row = new sbyte[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            row[i] = (sbyte)rng.Next(-127, 128);
        }

        var packed = new byte[EmbeddingMath.PackedStride(dimensions)];
        var step = EmbeddingMath.PackQuantized(row, packed);
        var unpacked = new sbyte[dimensions];
        EmbeddingMath.UnpackQuantized(packed, unpacked);

        Assert.Equal(127f / EmbeddingMath.PackedLevels, step);
        for (var i = 0; i < dimensions; i++)
        {
            // The level a round trip returns is the one the packer chose for that element, and the
            // value it stands for is within half a step of the int8 it replaced. Asserting the
            // level rather than the value is what makes this a test of the CODEC rather than of the
            // quantization error, which the eval measures instead.
            var expected = Math.Clamp((int)MathF.Round(row[i] / step), -EmbeddingMath.PackedLevels, EmbeddingMath.PackedLevels);
            Assert.Equal(expected, unpacked[i]);
            Assert.True(Math.Abs((unpacked[i] * step) - row[i]) <= (step / 2) + 0.001f);
        }
    }

    /// <summary>
    /// Below <see cref="EmbeddingMath.PackMinimumDimensions"/> nothing is packed, because the error
    /// only averages out across a wide dot product. A narrow row has to survive the round trip
    /// exactly, not approximately.
    /// </summary>
    [Fact]
    public void PackQuantized_LeavesNarrowRowsAlone()
    {
        var row = new sbyte[] { 127, -127, 1, 0, -64, 63, 100, -3 };
        var packed = new byte[EmbeddingMath.PackedStride(row.Length)];

        Assert.Equal(row.Length, packed.Length);
        Assert.Equal(1f, EmbeddingMath.PackQuantized(row, packed));

        var unpacked = new sbyte[row.Length];
        EmbeddingMath.UnpackQuantized(packed, unpacked);
        Assert.Equal(row, unpacked);
    }

    /// <summary>
    /// The whole justification for packing: the per-element error averages out of a dot product
    /// this wide. Measured over 200 random unit pairs rather than asserted on one, because a single
    /// pair says nothing about a distribution - and the bound is on the MEAN, since ranking depends
    /// on how candidates compare to each other rather than on any one cosine being exact.
    ///
    /// <para>
    /// The real numbers on this seed are a mean absolute error around 0.005 and a worst case around
    /// 0.012, against int8 on the same pairs. That is far coarser than int8's own error and is why
    /// this is pinned by measurement rather than by a round number: the eval is what established
    /// that the ranking does not care (nDCG@40 within [-0.0013, +0.0002] of int8 over 400 requests
    /// on an independent grader), and this only has to catch a codec change that made it worse.
    /// </para>
    /// </summary>
    [Fact]
    public void PackQuantized_KeepsTheCosineCloseEnoughToRank()
    {
        var rng = new Random(7);
        const int Dimensions = 768;
        var errors = new List<float>();

        for (var pair = 0; pair < 200; pair++)
        {
            var a = new float[Dimensions];
            var b = new float[Dimensions];
            for (var i = 0; i < Dimensions; i++)
            {
                a[i] = (float)((rng.NextDouble() * 2) - 1);
                b[i] = (float)((rng.NextDouble() * 2) - 1);
            }

            EmbeddingMath.NormalizeInPlace(a);
            EmbeddingMath.NormalizeInPlace(b);

            var rowInt8 = new sbyte[Dimensions];
            var rowScale = EmbeddingMath.Quantize(a, rowInt8);
            var query = EmbeddingMath.QuantizeQuery(b, out var queryScale);
            var int8Cosine = EmbeddingMath.QuantizedDot(query, queryScale, rowInt8, rowScale);

            var packed = new byte[EmbeddingMath.PackedStride(Dimensions)];
            var step = EmbeddingMath.PackQuantized(rowInt8, packed);
            var unpacked = new sbyte[Dimensions];
            EmbeddingMath.UnpackQuantized(packed, unpacked);
            var packedCosine = EmbeddingMath.QuantizedDot(query, queryScale, unpacked, rowScale * step);

            errors.Add(Math.Abs(packedCosine - int8Cosine));
        }

        Assert.True(errors.Average() < 0.008f, $"mean absolute cosine error {errors.Average()}");
        Assert.True(errors.Max() < 0.02f, $"worst absolute cosine error {errors.Max()}");
    }
}
