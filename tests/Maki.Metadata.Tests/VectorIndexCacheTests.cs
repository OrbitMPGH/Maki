using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

public class VectorIndexCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dumpPath;
    private readonly string _vectorPath;

    public VectorIndexCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-vindex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dumpPath = Path.Combine(_dir, "mangabaka.db");
        _vectorPath = Path.Combine(_dir, "embeddings.db");

        // Only the columns the index build reads; the real dump has ~130.
        using var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series (
                id INTEGER PRIMARY KEY, state TEXT, rating REAL, content_rating TEXT, type TEXT,
                status TEXT, year INTEGER, total_chapters TEXT, genres TEXT, authors TEXT,
                popularity_global_current INTEGER);
            INSERT INTO series VALUES (1, 'active', 80, 'safe', 'manga', 'completed', 1999, '12', '["Action"]', '["Miura"]', 3);
            INSERT INTO series VALUES (2, 'active', 70, 'safe', 'manhwa', 'releasing', 2015, '30.5', '["Romance"]', '["Miura","Other"]', 240);
            INSERT INTO series VALUES (3, 'active', 60, 'safe', 'manga', 'completed', NULL, NULL, NULL, NULL, NULL);
            INSERT INTO series VALUES (4, 'active', 90, 'pornographic', 'manga', 'completed', 2000, '5', NULL, NULL, 9);
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Build_ReadsCandidatesAndTheirDumpColumns()
    {
        Store().UpsertBatch([
            (1L, "h", [1f, 0f, 0f, 0f]),
            (2L, "h", [0f, 1f, 0f, 0f]),
            (3L, "h", [0f, 0f, 1f, 0f]),
        ]);

        var index = await Cache(dimensions: 4).GetAsync();

        Assert.NotNull(index);
        Assert.Equal(3, index!.Count);
        Assert.Equal(4, index.Dimensions);

        // Filters read the dump columns the build copied in.
        Assert.True(index.TryGetRow(2, out var manhwaRow));
        Assert.True(index.Matches(manhwaRow, index.Plan(new RecommendationFilters(Types: ["manhwa"]))));
        Assert.True(index.Matches(manhwaRow, index.Plan(new RecommendationFilters(MinChapters: 30))));
        Assert.True(index.TryGetRow(1, out var actionRow));
        Assert.True(index.Matches(actionRow, index.Plan(new RecommendationFilters(Genres: ["Action"]))));
        Assert.False(index.Matches(actionRow, index.Plan(new RecommendationFilters(Genres: ["Romance"]))));

        // Scoring columns: authors are interned and shared across rows, popularity is the raw
        // rank, and a null one reads as Unknown rather than 0 (which would be "most popular").
        Assert.True(index.TryGetAuthorId("Miura", out var miura));
        Assert.Contains(miura, index.AuthorsAt(actionRow));
        Assert.Contains(miura, index.AuthorsAt(manhwaRow));
        Assert.Equal(3, index.PopularityAt(actionRow));
        Assert.True(index.TryGetRow(3, out var sparseRow));
        Assert.Equal(VectorIndex.Unknown, index.PopularityAt(sparseRow));
        Assert.Empty(index.AuthorsAt(sparseRow));
    }

    [Fact]
    public async Task Build_SkipsPornographicAndUnvectoredSeries()
    {
        Store().UpsertBatch([(1L, "h", [1f, 0f, 0f, 0f]), (4L, "h", [0f, 1f, 0f, 0f])]);

        var index = await Cache(dimensions: 4).GetAsync();

        Assert.Equal(1, index!.Count);
        Assert.Equal(1, index.IdAt(0));
    }

    [Fact]
    public async Task Build_KeepsOnlyVectorsMatchingTheConfiguredModelWidth()
    {
        // What a model change looks like mid-migration: the table holds both widths at once.
        Store().UpsertBatch([
            (1L, "old", [1f, 0f, 0f, 0f]),          // previous model, 4 dims
            (2L, "new", [0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f]), // current model, 8 dims
            (3L, "new", [0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f]),
        ]);

        var index = await Cache(dimensions: 8).GetAsync();

        Assert.NotNull(index);
        Assert.Equal(8, index!.Dimensions);
        Assert.Equal([2L, 3L], Enumerable.Range(0, index.Count).Select(index.IdAt));
    }

    [Fact]
    public async Task Build_AllVectorsWrongWidth_IsEmptyRatherThanCorrupt()
    {
        Store().UpsertBatch([(1L, "old", [1f, 0f, 0f, 0f])]);

        Assert.Null(await Cache(dimensions: 8).GetAsync());
    }

    [Fact]
    public async Task Invalidate_ForcesARebuild()
    {
        var store = Store();
        store.UpsertBatch([(1L, "h", [1f, 0f, 0f, 0f])]);
        var cache = Cache(dimensions: 4);

        var first = await cache.GetAsync();
        Assert.Equal(1, first!.Count);
        Assert.Same(first, await cache.GetAsync()); // cached instance, not rebuilt

        store.UpsertBatch([(2L, "h", [0f, 1f, 0f, 0f])]);
        Assert.Equal(1, (await cache.GetAsync())!.Count); // still the stale index
        cache.Invalidate();
        Assert.Equal(2, (await cache.GetAsync())!.Count);
    }

    [Fact]
    public async Task NoVectorDb_IsNull() =>
        Assert.Null(await new VectorIndexCache(
            new EmbeddingOptions(_dir, Path.Combine(_dir, "missing.db"), _dir, EmbeddingModelProfile.Base),
            new MangaBakaDumpOptions(_dumpPath, _dir),
            NullLogger<VectorIndexCache>.Instance).GetAsync());

    private EmbeddingStore Store()
    {
        var store = new EmbeddingStore(new EmbeddingOptions(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base));
        store.EnsureSchema();
        return store;
    }

    private VectorIndexCache Cache(int dimensions) =>
        new(new EmbeddingOptions(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = dimensions }),
            new MangaBakaDumpOptions(_dumpPath, _dir),
            NullLogger<VectorIndexCache>.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
