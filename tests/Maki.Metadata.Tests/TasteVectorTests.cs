using Maki.Metadata.Embedding;
using Maki.Metadata.Taste;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The behavioural channel. Its whole contract is that an absent artifact costs nothing and a
/// present one can only add, so most of these pin an absence rather than a behaviour.
/// </summary>
public class TasteVectorTests
{
    private const int Dim = 8;

    [Fact]
    public void NoLayer_MeansNoTasteAnywhere()
    {
        var index = Build([[1f, 0, 0, 0, 0, 0, 0, 0], [0, 1f, 0, 0, 0, 0, 0, 0]]);

        Assert.Null(index.Taste);
        Assert.False(index.HasTasteAt(0));
        Assert.Null(index.TasteVectorAt(0));
        // Zero, not a NaN and not a penalty: a row nobody has behavioural data for has to be
        // rankable on everything else.
        Assert.Equal(0, index.TasteCosineAt(0, new sbyte[Dim], 1f));
    }

    [Fact]
    public void ARowTheArtifactDoesNotCover_ScoresZeroRatherThanBeingPenalised()
    {
        // Row 0 covered, row 1 not. Scale 0 is the layer's "absent" marker.
        var taste = Layer([[1f, 0, 0, 0], null]);
        var index = Build([[1f, 0, 0, 0, 0, 0, 0, 0], [0, 1f, 0, 0, 0, 0, 0, 0]], taste: taste);

        Assert.True(index.HasTasteAt(0));
        Assert.False(index.HasTasteAt(1));

        var query = EmbeddingMath.QuantizeQuery([1f, 0, 0, 0], out var scale);
        Assert.True(index.TasteCosineAt(0, query, scale) > 0.9);
        Assert.Equal(0, index.TasteCosineAt(1, query, scale));
    }

    [Fact]
    public void HybridScore_AddsTheBehaviouralTermSeparately()
    {
        var w = new EmbeddingMath.Weights(
            Semantic: 1, Genre: 0, Tag: 0, Author: 0, Quality: 0, Obscurity: 0, Taste: 2);

        var without = EmbeddingMath.HybridScore(0.5, 0, 0, false, 0, 0, 0.5, w);
        var with = EmbeddingMath.HybridScore(0.5, 0, 0, false, 0, 0, 0.5, w, tasteCosine: 0.25);

        // It is its own term, not folded into the semantic cosine: the two answer the same question
        // from different evidence and a candidate both agree on should be paid for twice.
        Assert.Equal(0.5, without, 6);
        Assert.Equal(0.5 + (2 * 0.25), with, 6);
    }

    [Fact]
    public void HybridScore_IgnoresTheBehaviouralTermAtWeightZero()
    {
        var w = new EmbeddingMath.Weights(Semantic: 1, Genre: 0, Tag: 0, Author: 0, Quality: 0, Obscurity: 0);

        // The shipped default for Taste is 0, exactly like Graph and CoRead, so an install with no
        // artifact scores identically to one built before the channel existed.
        Assert.Equal(0.0, w.Taste);
        Assert.Equal(
            EmbeddingMath.HybridScore(0.5, 0, 0, false, 0, 0, 0.5, w),
            EmbeddingMath.HybridScore(0.5, 0, 0, false, 0, 0, 0.5, w, tasteCosine: 0.9),
            6);
    }

    [Fact]
    public void Injection_RequiresCorroboration()
    {
        // One channel of text cosines; every row survived the filter (no -inf).
        var cosines = new[] { new[] { 0.1f, 0.1f, 0.1f, 0.1f } };
        var pooled = new List<int> { 0 };
        var taste = new[] { 0f, 1.0f, 0.5f, 0.9f };

        var injected = SemanticRecommender.InjectTasteCandidates(
            cosines, pooled, [], taste, TasteVectorTuning.Default with { MinInjectedScore = 0.7 });

        // Best is 1.0, so the floor is 0.7: row 3 clears it, row 2 does not, row 1 is the best but
        // is not already pooled so it comes too.
        Assert.Equal([1, 3], injected);
    }

    [Fact]
    public void Injection_NeverAdmitsARowTheFilterRejected()
    {
        // Row 1 failed the filter plan, which Scan records as negative infinity in every channel.
        // Re-testing RecommendationFilters here would be a third copy of that logic and a way to
        // smuggle a row past one, so the injector reads the sentinel instead.
        var cosines = new[] { new[] { 0.1f, float.NegativeInfinity } };
        var taste = new[] { 0.1f, 1.0f };

        var injected = SemanticRecommender.InjectTasteCandidates(
            cosines, [0], [], taste, TasteVectorTuning.Default);

        Assert.Empty(injected);
    }

    [Fact]
    public void Injection_IsOffWhenTheChannelIs()
    {
        var cosines = new[] { new[] { 0.1f, 0.1f } };
        var taste = new[] { 0f, 1.0f };

        Assert.Empty(SemanticRecommender.InjectTasteCandidates(
            cosines, [0], [], taste, TasteVectorTuning.Default with { Weight = 0 }));
        Assert.Empty(SemanticRecommender.InjectTasteCandidates(
            cosines, [0], [], taste, TasteVectorTuning.Default with { MaxInjected = 0 }));
    }

    [Fact]
    public void Injection_SkipsRowsAnotherChannelAlreadyInjected()
    {
        var cosines = new[] { new[] { 0.1f, 0.1f, 0.1f } };
        var taste = new[] { 0f, 1.0f, 0.95f };

        // Row 1 was already brought in by a crowd graph; this channel must not add it twice, and
        // the cap it spends is its own.
        var injected = SemanticRecommender.InjectTasteCandidates(
            cosines, [0], [1], taste, TasteVectorTuning.Default);

        Assert.Equal([2], injected);
    }

    private static TasteLayer Layer(float[]?[] vectors)
    {
        const int dims = 4;
        var data = new sbyte[vectors.Length * dims];
        var scales = new float[vectors.Length];
        var covered = 0;
        for (var i = 0; i < vectors.Length; i++)
        {
            if (vectors[i] is not { } vec)
            {
                continue;
            }

            scales[i] = EmbeddingMath.Quantize(vec, data.AsSpan(i * dims, dims));
            covered++;
        }

        return new TasteLayer(data, scales, dims, covered);
    }

    private static VectorIndex Build(float[][] vectors, TasteLayer? taste = null)
    {
        var count = vectors.Length;
        var data = new sbyte[count * Dim];
        var scales = new float[count];
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = 100 + i;
            scales[i] = EmbeddingMath.Quantize(vectors[i], data.AsSpan(i * Dim, Dim));
        }

        var columns = new VectorIndexColumns(
            new int[count], new float[count], new int[count], new byte[count], new byte[count],
            [.. Enumerable.Repeat(Array.Empty<int>(), count)],
            [.. Enumerable.Repeat(Array.Empty<int>(), count)],
            [.. Enumerable.Repeat(Array.Empty<int>(), count)],
            new int[count], new byte[]?[count], new byte[count],
            [.. Enumerable.Repeat(VectorIndex.Unknown, count)]);
        var vocabularies = new VectorIndexVocabularies(
            new Dictionary<string, byte>(), new Dictionary<string, byte>(),
            new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int[]>(), new Dictionary<string, byte>());

        return new VectorIndex(ids, data, scales, Dim, columns, vocabularies, taste);
    }
}
