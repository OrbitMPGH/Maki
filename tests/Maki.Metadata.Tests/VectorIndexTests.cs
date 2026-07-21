using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Xunit;

namespace Maki.Metadata.Tests;

public class VectorIndexTests
{
    private const int Dim = 8;

    [Fact]
    public void Quantize_RoundTrips_WithinTolerance()
    {
        var random = new Random(1234);
        var query = UnitVector(random, 64);
        var candidate = UnitVector(random, 64);

        var packed = new sbyte[64];
        var scale = EmbeddingMath.Quantize(candidate, packed);
        var approx = EmbeddingMath.QuantizedDot(query, packed, scale, new float[64]);

        // int8 over a unit vector keeps ~3 decimal digits — far finer than the gap between
        // neighbouring search results.
        Assert.Equal(EmbeddingMath.Cosine(query, candidate), approx, 3);
    }

    [Fact]
    public void Quantize_ZeroVector_IsHandled()
    {
        var packed = new sbyte[4];
        Assert.Equal(0f, EmbeddingMath.Quantize(new float[4], packed));
        Assert.Equal(0f, EmbeddingMath.QuantizedDot([1f, 0f, 0f, 0f], packed, 0f, new float[4]));
    }

    [Fact]
    public void Search_RanksByCosine_AndHonoursTake()
    {
        // Row 0 points exactly at the query, row 1 is 45° off, row 2 is orthogonal.
        var index = Build(
            [Axis(0), Diagonal(0, 1), Axis(1)],
            years: [2000, 2000, 2000]);

        var hits = index.Search(Axis(0), FilterPlan.None, take: 2);

        Assert.Equal([0, 1], hits.Select(h => h.Row));
        Assert.Equal(1f, hits[0].Cosine, 2);
        Assert.True(hits[0].Cosine > hits[1].Cosine);
    }

    [Fact]
    public void Search_SkipsFilteredRows()
    {
        var index = Build([Axis(0), Axis(0)], years: [1995, 2015]);
        var plan = index.Plan(new RecommendationFilters(YearMin: 2000));

        var hits = index.Search(Axis(0), plan, take: 10);

        Assert.Equal([1], hits.Select(h => h.Row));
    }

    [Fact]
    public void YearBounds_ExcludeUnknownYears()
    {
        var index = Build([Axis(0)], years: [VectorIndex.Unknown]);

        Assert.False(index.Matches(0, index.Plan(new RecommendationFilters(YearMin: 1900))));
        Assert.False(index.Matches(0, index.Plan(new RecommendationFilters(YearMax: 2100))));
        Assert.True(index.Matches(0, FilterPlan.None));
    }

    [Fact]
    public void ChapterBounds_ExcludeUnknownCounts()
    {
        var index = Build([Axis(0), Axis(0)], chapters: [VectorIndex.Unknown, 50]);
        var plan = index.Plan(new RecommendationFilters(MinChapters: 10, MaxChapters: 100));

        Assert.False(index.Matches(0, plan));
        Assert.True(index.Matches(1, plan));
    }

    [Fact]
    public void Genres_MustAllBePresent_AndAreCaseInsensitive()
    {
        var index = Build(
            [Axis(0), Axis(0)],
            genres: [["Action", "Romance"], ["Action"]]);

        var both = index.Plan(new RecommendationFilters(Genres: ["action", "romance"]));
        Assert.True(index.Matches(0, both));
        Assert.False(index.Matches(1, both));
    }

    [Fact]
    public void UnknownGenre_MakesThePlanImpossible()
    {
        var index = Build([Axis(0)], genres: [["Action"]]);

        var plan = index.Plan(new RecommendationFilters(Genres: ["Nonexistent"]));

        Assert.True(plan.Impossible);
        Assert.False(index.Matches(0, plan));
        Assert.Empty(index.Search(Axis(0), plan, take: 10));
    }

    [Fact]
    public void Types_AreADisjunction()
    {
        var index = Build([Axis(0), Axis(0)], types: ["manga", "manhwa"]);
        var plan = index.Plan(new RecommendationFilters(Types: ["manhwa", "manhua"]));

        Assert.False(plan.Impossible); // "manhua" is unknown here, but "manhwa" still matches
        Assert.False(index.Matches(0, plan));
        Assert.True(index.Matches(1, plan));
    }

    [Fact]
    public void MinRating_Filters()
    {
        var index = Build([Axis(0), Axis(0)], ratings: [60f, 90f]);
        var plan = index.Plan(new RecommendationFilters(MinRating: 80));

        Assert.False(index.Matches(0, plan));
        Assert.True(index.Matches(1, plan));
    }

    [Fact]
    public void TryGetRow_MapsIdsBack()
    {
        var index = Build([Axis(0), Axis(1)]);

        Assert.True(index.TryGetRow(101, out var row));
        Assert.Equal(1, row);
        Assert.Equal(100, index.IdAt(0));
        Assert.False(index.TryGetRow(999, out _));
    }

    /// <summary>Builds an index over the given unit vectors; ids are 100, 101, … by row.</summary>
    private static VectorIndex Build(
        float[][] vectors,
        int[]? years = null,
        float[]? ratings = null,
        int[]? chapters = null,
        string[]? types = null,
        string[][]? genres = null)
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

        var typeIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var statusIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { ["releasing"] = 0 };
        var genreIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var typeIdx = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var name = types?[i] ?? "manga";
            if (!typeIds.TryGetValue(name, out var id))
            {
                id = (byte)typeIds.Count;
                typeIds[name] = id;
            }

            typeIdx[i] = id;
        }

        var genreIdx = new int[count][];
        for (var i = 0; i < count; i++)
        {
            var names = genres?[i] ?? [];
            genreIdx[i] = names.Select(name =>
            {
                if (!genreIds.TryGetValue(name, out var id))
                {
                    id = genreIds.Count;
                    genreIds[name] = id;
                }

                return id;
            }).ToArray();
        }

        return new VectorIndex(
            ids,
            data,
            scales,
            Dim,
            years ?? Enumerable.Repeat(2010, count).ToArray(),
            ratings ?? Enumerable.Repeat(75f, count).ToArray(),
            chapters ?? Enumerable.Repeat(100, count).ToArray(),
            typeIdx,
            new byte[count],
            genreIdx,
            typeIds,
            statusIds,
            genreIds);
    }

    private static float[] Axis(int i)
    {
        var v = new float[Dim];
        v[i] = 1f;
        return v;
    }

    private static float[] Diagonal(int a, int b)
    {
        var v = new float[Dim];
        v[a] = v[b] = 1f;
        EmbeddingMath.NormalizeInPlace(v);
        return v;
    }

    private static float[] UnitVector(Random random, int dim)
    {
        var v = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            v[i] = (float)((random.NextDouble() * 2) - 1);
        }

        EmbeddingMath.NormalizeInPlace(v);
        return v;
    }
}
