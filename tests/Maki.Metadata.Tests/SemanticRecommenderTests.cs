using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// End-to-end over a tiny fake dump plus a real vector store, so the retrieval shape is exercised
/// through the same code path the app uses (index build included) rather than against a stub.
/// </summary>
public class SemanticRecommenderTests : IDisposable
{
    private const int Dim = 16;

    private readonly string _dir;
    private readonly string _dumpPath;
    private readonly string _vectorPath;
    private readonly List<string> _rows = [];

    public SemanticRecommenderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-semreco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dumpPath = Path.Combine(_dir, "mangabaka.db");
        _vectorPath = Path.Combine(_dir, "embeddings.db");
    }

    [Fact]
    public async Task ASeedWhoseTasteIsTwoThings_StillSurfacesAMatchForEitherHalf()
    {
        // Two seeds pointing in unrelated directions, so their centroid sits near neither. Twin is
        // a ~0.997 match for one seed but only ~0.70 against that centroid, while a crowd of
        // middling candidates sits at ~0.90 against the centroid and ~0.64 against either seed.
        // Ranked by the centroid alone — what a single mean-vector query does — every middling
        // candidate outranks Twin and it never makes the page, which is the dilution being fixed.
        var alpha = Axis(0);
        var beta = Axis(1);
        var centroid = Blend(alpha, beta);

        var vectors = new List<(long Id, string Hash, float[] Vector)>
        {
            (1, "h", alpha),
            (2, "h", beta),
            (10, "h", Nudge(alpha, 2, 0.08f)),
        };
        Add(1, "Alpha");
        Add(2, "Beta");
        Add(10, "Twin of Alpha");
        for (var i = 0; i < 8; i++)
        {
            Add(100 + i, $"Middling {i}");
            vectors.Add((100 + i, "h", Nudge(centroid, 3 + i, 0.48f)));
        }

        WriteDump();
        Store().UpsertBatch(vectors);

        var picks = await Recommender().GetSimilarAsync([1, 2], [], limit: 3);

        Assert.Equal("10", picks[0].ProviderId);
        // And the attribution names the seed that actually drove it, not the whole seed set.
        Assert.Equal("Alpha", picks[0].BecauseOfTitle);
        // The middlings are still recommendable, just no longer ahead of a better match.
        Assert.Equal(3, picks.Count);
        Assert.All(picks.Skip(1), p => Assert.StartsWith("Middling", p.Title));
    }

    [Fact]
    public async Task Seeds_AndExplicitExclusions_AreNeverRecommendedBack()
    {
        Add(1, "Alpha");
        Add(10, "Excluded");
        Add(11, "Wanted");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.1f)),
            (11L, "h", Nudge(Axis(0), 3, 0.1f)),
        ]);

        var picks = await Recommender().GetSimilarAsync([1], [10], limit: 10);

        Assert.Equal(["11"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task Diversity_SwapsANearDuplicateForSomethingElseThatMatches()
    {
        // Pair and PairTwin are all but the same vector; Other is a weaker match but unrelated to
        // both. At diversity 0 the pair takes both slots, which is the homogeneity complaint.
        var seed = Spread(0, 1, 2, 3);
        Add(1, "Seed");
        Add(10, "Pair");
        Add(11, "Pair Twin");
        Add(12, "Other");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", seed),
            (10L, "h", Spread(0, 1, 2)),
            (11L, "h", Nudge(Spread(0, 1, 2), 8, 0.02f)),
            (12L, "h", Blend(Axis(3), Scaled(Axis(0), 0.3f))),
        ]);

        var packed = await Recommender().GetSimilarAsync([1], [], limit: 2);
        var spread = await Recommender().GetSimilarAsync([1], [], limit: 2, diversity: 0.6);

        Assert.Equal(["10", "11"], packed.Select(p => p.ProviderId));
        Assert.Equal(["10", "12"], spread.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task FiltersAreAppliedBeforeTheTopK_SoAFilteredQueryIsNotTruncatedToNothing()
    {
        // Every close match is a manhwa; the one manga is the distant candidate. A filter applied
        // to an already-chosen top-K would leave nothing.
        Add(1, "Seed");
        for (var i = 0; i < 6; i++)
        {
            Add(10 + i, $"Near {i}", type: "manhwa");
        }

        Add(50, "Distant Manga");
        WriteDump();
        var vectors = new List<(long, string, float[])> { (1L, "h", Axis(0)), (50L, "h", Spread(0, 1, 2)) };
        for (var i = 0; i < 6; i++)
        {
            vectors.Add((10 + i, "h", Nudge(Axis(0), 4 + i, 0.05f)));
        }

        Store().UpsertBatch(vectors);

        var picks = await Recommender().GetSimilarAsync(
            [1], [], limit: 3, new RecommendationFilters(Types: ["manga"]));

        Assert.Equal(["50"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task NoIndexYet_ReturnsNothingSoTheCallerCanFallBack()
    {
        Add(1, "Seed");
        WriteDump();
        Store(); // schema only — no vectors

        Assert.Empty(await Recommender().GetSimilarAsync([1], [], limit: 5));
    }

    private void Add(long id, string title, string type = "manga", string genres = """["Action"]""") =>
        _rows.Add(
            $"({id}, 'active', 80, 'safe', '{type}', 'completed', 2000, '{title}', " +
            $"'http://c/{id}.jpg', 'desc', '12', '{genres}', '[\"Author\"]', {id})");

    private void WriteDump()
    {
        using var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series (
                id INTEGER PRIMARY KEY, state TEXT, rating REAL, content_rating TEXT, type TEXT,
                status TEXT, year INTEGER, title TEXT, cover_raw_url TEXT, description TEXT,
                total_chapters TEXT, genres TEXT, authors TEXT, popularity_global_current INTEGER,
                -- The pre-sized thumbnail columns the hydrate query reads. Named here rather than
                -- listed in the INSERT, so adding a column to the dump doesn't mean editing every
                -- row literal in this file.
                cover_x250_x1 TEXT, cover_x250_x2 TEXT);
            """ + $"""
            INSERT INTO series (
                id, state, rating, content_rating, type, status, year, title, cover_raw_url,
                description, total_chapters, genres, authors, popularity_global_current)
            VALUES {string.Join(",", _rows)};
            """;
        cmd.ExecuteNonQuery();
    }

    private EmbeddingOptions Options() =>
        new(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = Dim }) { Enabled = true };

    private EmbeddingStore Store()
    {
        var store = new EmbeddingStore(Options());
        store.EnsureSchema();
        return store;
    }

    private SemanticRecommender Recommender()
    {
        var dump = new MangaBakaDumpOptions(_dumpPath, _dir);
        return new SemanticRecommender(
            Options(),
            dump,
            new EmbeddingStore(Options()),
            new VectorIndexCache(Options(), dump, NullLogger<VectorIndexCache>.Instance),
            NullLogger<SemanticRecommender>.Instance);
    }

    private static float[] Axis(int i)
    {
        var v = new float[Dim];
        v[i] = 1f;
        return v;
    }

    /// <summary>A unit vector with equal weight on each named axis.</summary>
    private static float[] Spread(params int[] axes)
    {
        var v = new float[Dim];
        foreach (var a in axes)
        {
            v[a] = 1f;
        }

        EmbeddingMath.NormalizeInPlace(v);
        return v;
    }

    private static float[] Blend(float[] a, float[] b) => EmbeddingMath.Mean([a, b])!;

    private static float[] Scaled(float[] v, float by) => v.Select(x => x * by).ToArray();

    /// <summary>The vector, tilted slightly onto one other axis, so candidates aren't identical.</summary>
    private static float[] Nudge(float[] v, int axis, float amount)
    {
        var copy = (float[])v.Clone();
        copy[axis] += amount;
        EmbeddingMath.NormalizeInPlace(copy);
        return copy;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
