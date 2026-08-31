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
        var packedQuery = EmbeddingMath.QuantizeQuery(query, out var queryScale);
        var approx = EmbeddingMath.QuantizedDot(packedQuery, queryScale, packed, scale);

        // int8 on both sides of the dot still keeps ~3 decimal digits over unit vectors — far
        // finer than the gap between neighbouring search results.
        Assert.Equal(EmbeddingMath.Cosine(query, candidate), approx, 3);
    }

    [Fact]
    public void QuantizedDot_MatchesTheFloatDot_AcrossVectorWidths()
    {
        // Widths that do and don't divide evenly into a SIMD register, so the scalar tail is
        // covered as well as the vector body.
        var random = new Random(99);
        foreach (var dim in new[] { 7, 16, 33, 64, 768 })
        {
            var a = UnitVector(random, dim);
            var b = UnitVector(random, dim);
            var packedA = EmbeddingMath.QuantizeQuery(a, out var scaleA);
            var packedB = EmbeddingMath.QuantizeQuery(b, out var scaleB);

            Assert.Equal(
                EmbeddingMath.Cosine(a, b),
                EmbeddingMath.QuantizedDot(packedA, scaleA, packedB, scaleB),
                2);
        }
    }

    [Fact]
    public void Quantize_ZeroVector_IsHandled()
    {
        var packed = new sbyte[4];
        Assert.Equal(0f, EmbeddingMath.Quantize(new float[4], packed));

        var query = EmbeddingMath.QuantizeQuery([1f, 0f, 0f, 0f], out var scale);
        Assert.Equal(0f, EmbeddingMath.QuantizedDot(query, scale, packed, 0f));
    }

    [Fact]
    public void CosineBetween_MatchesTheRowsTheIndexHolds()
    {
        var index = Build([Axis(0), Diagonal(0, 1), Axis(1)]);

        Assert.Equal(1f, index.CosineBetween(0, 0), 2);
        Assert.Equal(0.707f, index.CosineBetween(0, 1), 2);
        Assert.Equal(0f, index.CosineBetween(0, 2), 2);
    }

    [Fact]
    public void AuthorsAndPopularity_AreReadableForScoring()
    {
        var index = Build(
            [Axis(0), Axis(1)],
            authors: [["Kentaro Miura"], []],
            popularity: [12, VectorIndex.Unknown]);

        Assert.True(index.TryGetAuthorId("kentaro miura", out var id));
        Assert.Equal([id], index.AuthorsAt(0));
        Assert.Empty(index.AuthorsAt(1));
        Assert.Equal(12, index.PopularityAt(0));
        Assert.Equal(VectorIndex.Unknown, index.PopularityAt(1));
        Assert.False(index.TryGetAuthorId("Nobody", out _));
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

    /// <summary>
    /// The whole point of the tag filter living in the index rather than being applied to the
    /// result page: it has to narrow what gets *scored*, so a filtered search returns a full page
    /// from inside the tag. Post-filtering could only ever remove rows the other channels had
    /// already ranked, which turned "search within this tag" into "delete most of the page".
    /// </summary>
    [Fact]
    public void Search_WithATagFilter_RanksWithinTheTag_NotTheUnfilteredPage()
    {
        // Rows 0 and 1 are the closest to the query but carry the wrong tag; rows 2 and 3 carry it.
        var index = Build(
            [Axis(0), Axis(0), Axis(1), Axis(1)],
            tags: [["Isekai"], ["Isekai"], ["Childhood Friends"], ["Childhood Friends"]]);

        var plan = index.Plan(new RecommendationFilters(Tags: ["Childhood Friends"]));
        var hits = index.Search(Axis(0), plan, take: 4);

        Assert.Equal([102L, 103L], hits.Select(h => index.IdAt(h.Row)).Order());
    }

    [Fact]
    public void Plan_WithATagNameTheVocabularyDoesNotHave_IsImpossible()
    {
        var index = Build([Axis(0)], tags: [["Isekai"]]);

        Assert.True(index.Plan(new RecommendationFilters(Tags: ["No Such Tag"])).Impossible);
        // Casing is not a mismatch — the vocabulary interns variants separately and the lookup is
        // case-insensitive, same as the SQL clause.
        Assert.False(index.Plan(new RecommendationFilters(Tags: ["isekai"])).Impossible);
    }

    [Fact]
    public void Plan_WithSeveralTags_RequiresAllOfThem()
    {
        var index = Build(
            [Axis(0), Axis(0)],
            tags: [["Isekai", "Revenge"], ["Isekai"]]);

        var plan = index.Plan(new RecommendationFilters(Tags: ["Isekai", "Revenge"]));

        Assert.True(index.Matches(0, plan));
        Assert.False(index.Matches(1, plan));
    }

    [Fact]
    public void BuildRowMask_ignoresIdsTheIndexDoesNotCarry()
    {
        var index = Build([Axis(0), Axis(1), Axis(2)]);

        // 999 is not indexed (unrated, or a novel); asking for it must not shift anyone else's row.
        var mask = index.BuildRowMask([101L, 999L]);

        Assert.Equal([false, true, false], mask);
    }

    [Fact]
    public void Matches_RejectsRowsOutsideTheCreditMask()
    {
        var index = Build([Axis(0), Axis(1)]);
        var plan = FilterPlan.None with { CreditMask = index.BuildRowMask([101L]) };

        Assert.False(index.Matches(0, plan));
        Assert.True(index.Matches(1, plan));
    }

    /// <summary>
    /// The invariant CLAUDE.md states for every filter, made executable. A credit applied to the
    /// result page instead could only ever remove rows the channels happened to rank, so
    /// <c>author:X</c> would narrow a page rather than search within that author's work.
    /// </summary>
    [Fact]
    public void ACreditMaskAppliesBeforeTopK()
    {
        // Row 0 is the closest match by a mile, and the mask excludes it. A post-filter would
        // return an empty page after top-K picked row 0; a pre-filter returns the masked row.
        var index = Build([Axis(0), Axis(1), Axis(2)]);
        var plan = FilterPlan.None with { CreditMask = index.BuildRowMask([102L]) };

        var hits = index.Search(Axis(0), plan, take: 10);

        Assert.Equal(102L, index.IdAt(Assert.Single(hits).Row));
    }

    [Fact]
    public void AnEmptyCreditMaskMatchesNothing()
    {
        var index = Build([Axis(0), Axis(1)]);
        var plan = FilterPlan.None with { CreditMask = index.BuildRowMask([]) };

        Assert.Empty(index.Search(Axis(0), plan, take: 10));
    }

    /// <summary>Builds an index over the given unit vectors; ids are 100, 101, … by row.</summary>
    private static VectorIndex Build(
        float[][] vectors,
        int[]? years = null,
        float[]? ratings = null,
        int[]? chapters = null,
        string[]? types = null,
        string[][]? genres = null,
        string[][]? authors = null,
        string[][]? artists = null,
        int[]? popularity = null,
        string[][]? tags = null,
        int[]? franchise = null)
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
        var contentRatingIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { ["safe"] = 0 };

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

        var authorIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genreIdx = Intern(genres, count, genreIds);
        var authorIdx = Intern(authors, count, authorIds);
        var artistIdx = Intern(artists, count, authorIds);

        // Tags go in as packed blobs plus a name → ids vocabulary, the same two pieces
        // VectorIndexCache assembles from series_tags and tag_vocab.
        var tagIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagIdx = Intern(tags, count, tagIds);
        var tagBlobs = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            tagBlobs[i] = TagMath.Pack([.. tagIdx[i].Select(id => (id, TagMath.ClassOf("core")))]);
        }

        var tagVocab = tagIds.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }, StringComparer.OrdinalIgnoreCase);

        return new VectorIndex(
            ids,
            data,
            scales,
            Dim,
            new VectorIndexColumns(
                years ?? Enumerable.Repeat(2010, count).ToArray(),
                ratings ?? Enumerable.Repeat(75f, count).ToArray(),
                chapters ?? Enumerable.Repeat(100, count).ToArray(),
                typeIdx,
                new byte[count],
                genreIdx,
                authorIdx,
                artistIdx,
                popularity ?? Enumerable.Repeat(1000, count).ToArray(),
                tagBlobs,
                new byte[count],
                franchise ?? Enumerable.Repeat(VectorIndex.Unknown, count).ToArray()),
            new VectorIndexVocabularies(typeIds, statusIds, genreIds, authorIds, tagVocab, contentRatingIds));
    }

    /// <summary>Interns a per-row list of names into the vocabulary, exactly as the cache build does.</summary>
    private static int[][] Intern(string[][]? names, int count, Dictionary<string, int> vocab)
    {
        var result = new int[count][];
        for (var i = 0; i < count; i++)
        {
            result[i] = (names?[i] ?? []).Select(name =>
            {
                if (!vocab.TryGetValue(name, out var id))
                {
                    id = vocab.Count;
                    vocab[name] = id;
                }

                return id;
            }).ToArray();
        }

        return result;
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
