using System.Collections.Concurrent;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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
                status TEXT, year INTEGER, total_chapters TEXT, genres TEXT, authors TEXT, artists TEXT,
                popularity_global_current INTEGER);
            INSERT INTO series VALUES (1, 'active', 80, 'safe', 'manga', 'completed', 1999, '12', '["Action"]', '["Miura"]', NULL, 3);
            INSERT INTO series VALUES (2, 'active', 70, 'safe', 'manhwa', 'releasing', 2015, '30.5', '["Romance"]', '["Miura","Other"]', NULL, 240);
            INSERT INTO series VALUES (3, 'active', 60, 'safe', 'manga', 'completed', NULL, NULL, NULL, NULL, NULL, NULL);
            INSERT INTO series VALUES (4, 'active', 90, 'pornographic', 'manga', 'completed', 2000, '5', NULL, NULL, NULL, 9);
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
        Assert.Contains(miura, index.AuthorsAt(actionRow).ToArray());
        Assert.Contains(miura, index.AuthorsAt(manhwaRow).ToArray());
        Assert.Equal(3, index.PopularityAt(actionRow));
        Assert.True(index.TryGetRow(3, out var sparseRow));
        Assert.Equal(VectorIndex.Unknown, index.PopularityAt(sparseRow));
        Assert.Empty(index.AuthorsAt(sparseRow).ToArray());
    }

    [Fact]
    public async Task Build_TrimsIneligibleAndMissingDumpRowsFromVectorCapacity()
    {
        using (var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE series SET type = 'novel' WHERE id = 1;
                UPDATE series SET state = 'merged' WHERE id = 2;
                UPDATE series SET rating = NULL WHERE id = 3;
                """;
            cmd.ExecuteNonQuery();
        }

        Store().UpsertBatch(Enumerable.Range(1, 5)
            .Select(id => ((long)id, "h", new[] { 1f, 0f, 0f, 0f })).ToList());

        var index = await Cache(dimensions: 4).GetAsync();

        Assert.NotNull(index);
        Assert.Equal(1, index.Count);
        Assert.Equal(4L, index.IdAt(0));
        Assert.Equal(2000, index.YearAt(0));
        Assert.Equal(90, index.RatingAt(0));
        Assert.Equal(9, index.PopularityAt(0));
        Assert.Equal([1f, 0f, 0f, 0f], index.VectorAt(0));
        Assert.False(index.TryGetRow(0, out _));
        Assert.False(index.TryGetRow(5, out _));
    }

    [Fact]
    public async Task Build_VectorsWithoutAnyEligibleDumpRows_IsEmpty()
    {
        Store().UpsertBatch([(999L, "h", [1f, 0f, 0f, 0f])]);

        Assert.Null(await Cache(dimensions: 4).GetAsync());
    }

    [Fact]
    public async Task Build_LoadsPornographicRowsButOnlyASearchWithThatCeilingMatchesThem()
    {
        Store().UpsertBatch([(1L, "h", [1f, 0f, 0f, 0f]), (4L, "h", [0f, 1f, 0f, 0f])]);

        var index = await Cache(dimensions: 4).GetAsync();

        // Loaded into the index like any other rated row — content rating is bounded per search
        // (see VectorIndex.Matches), not by excluding the row from the build.
        Assert.Equal(2, index!.Count);
        Assert.True(index.TryGetRow(4, out var pornographicRow));
        Assert.True(index.Matches(
            pornographicRow, index.Plan(new RecommendationFilters(ContentRatings: [ContentRating.Pornographic]))));
        Assert.False(index.Matches(
            pornographicRow, index.Plan(new RecommendationFilters(ContentRatings: [ContentRating.Safe]))));
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
    public async Task Franchises_LoadOnlyOnDemand_OnceAcrossConcurrentReaders()
    {
        AddFranchiseColumns();
        Store().UpsertBatch([
            (1L, "h", [1f, 0f, 0f, 0f]),
            (3L, "h", [0f, 1f, 0f, 0f]),
            (4L, "h", [0f, 0f, 1f, 0f]),
        ]);
        var logger = new RecordingLogger();
        var index = (await Cache(dimensions: 4, logger).GetAsync())!;

        Assert.DoesNotContain(logger.Messages, m => m.Contains("Franchise graph:"));
        Assert.True(index.TryGetRow(1, out var first));
        Assert.True(index.TryGetRow(3, out var last));
        Assert.True(index.TryGetRow(4, out var unrelated));

        var components = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => index.FranchiseAt(first))));

        Assert.NotEqual(VectorIndex.Unknown, components[0]);
        Assert.All(components, c => Assert.Equal(components[0], c));
        // The middle volume (2) has no vector, but still connects 1 and 3.
        Assert.Equal(components[0], index.FranchiseAt(last));
        Assert.Equal(VectorIndex.Unknown, index.FranchiseAt(unrelated));
        Assert.Single(logger.Messages, m => m.Contains("Franchise graph:"));
    }

    [Fact]
    public async Task Franchises_OldDumpWithoutRelationshipsFallsBackOnlyWhenRequested()
    {
        Store().UpsertBatch([(1L, "h", [1f, 0f, 0f, 0f])]);
        var logger = new RecordingLogger();
        var index = (await Cache(dimensions: 4, logger).GetAsync())!;

        Assert.DoesNotContain(logger.Messages, m => m.Contains("Could not build the franchise graph"));
        Assert.Equal(VectorIndex.Unknown, index.FranchiseAt(0));
        Assert.Equal(VectorIndex.Unknown, index.FranchiseAt(0));
        Assert.Single(logger.Messages, m => m.Contains("Could not build the franchise graph"));
    }

    [Fact]
    public async Task Franchises_RebuildAfterInvalidation_WithoutChangingAnAlreadyLoadedIndex()
    {
        AddFranchiseColumns();
        Store().UpsertBatch([(1L, "h", [1f, 0f, 0f, 0f])]);
        var logger = new RecordingLogger();
        var cache = Cache(dimensions: 4, logger);
        var first = (await cache.GetAsync())!;
        var original = first.FranchiseAt(0);
        Assert.NotEqual(VectorIndex.Unknown, original);

        using (var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE series SET relationships_sequel = NULL";
            cmd.ExecuteNonQuery();
        }

        cache.Invalidate();
        var rebuilt = (await cache.GetAsync())!;
        Assert.Single(logger.Messages, m => m.Contains("Franchise graph:"));
        Assert.Equal(VectorIndex.Unknown, rebuilt.FranchiseAt(0));
        Assert.Equal(original, first.FranchiseAt(0));
        Assert.Equal(2, logger.Messages.Count(m => m.Contains("Franchise graph:")));
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

    private VectorIndexCache Cache(int dimensions, ILogger<VectorIndexCache>? logger = null) =>
        new(new EmbeddingOptions(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = dimensions }),
            new MangaBakaDumpOptions(_dumpPath, _dir),
            logger ?? NullLogger<VectorIndexCache>.Instance);

    private void AddFranchiseColumns()
    {
        using var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE series ADD COLUMN relationships_v2 TEXT;
            ALTER TABLE series ADD COLUMN relationships_sequel TEXT;
            ALTER TABLE series ADD COLUMN relationships_prequel TEXT;
            ALTER TABLE series ADD COLUMN relationships_spin_off TEXT;
            ALTER TABLE series ADD COLUMN relationships_side_story TEXT;
            ALTER TABLE series ADD COLUMN relationships_main_story TEXT;
            UPDATE series SET relationships_sequel = '[2]' WHERE id = 1;
            UPDATE series SET relationships_sequel = '[3]' WHERE id = 2;
            """;
        cmd.ExecuteNonQuery();
    }

    private sealed class RecordingLogger : ILogger<VectorIndexCache>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

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
