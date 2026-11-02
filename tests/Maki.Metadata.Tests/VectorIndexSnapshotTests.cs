using System.Collections.Concurrent;
using System.Text;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// A reload after an idle unload reads the built index back from disk. It has to be the index a
/// build would have produced, and it has to be ignored the moment anything it was built from moves.
/// </summary>
public class VectorIndexSnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "maki-vsnap-" + Guid.NewGuid().ToString("N"))).FullName;

    private string DumpPath => Path.Combine(_dir, "mangabaka.db");
    private string VectorPath => Path.Combine(_dir, "embeddings.db");

    public VectorIndexSnapshotTests()
    {
        using var conn = new SqliteConnection($"Data Source={DumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series (
                id INTEGER PRIMARY KEY, state TEXT, rating REAL, content_rating TEXT, type TEXT,
                status TEXT, year INTEGER, total_chapters TEXT, genres TEXT, authors TEXT, artists TEXT,
                popularity_global_current INTEGER, published_start_date TEXT);
            INSERT INTO series VALUES (1, 'active', 80, 'safe', 'manga', 'completed', 1999, '12', '["Action","Drama"]', '["Miura"]', '["Miura"]', 3, '1999-04-01');
            INSERT INTO series VALUES (2, 'active', 70, 'erotica', 'manhwa', 'releasing', 2015, '30.5', '["Romance"]', '["Miura","Other"]', NULL, 240, NULL);
            INSERT INTO series VALUES (3, 'active', 60, 'safe', 'manga', 'completed', NULL, NULL, NULL, NULL, '["Artist"]', NULL, '2001-02-03');
            INSERT INTO series VALUES (4, 'active', 90, 'pornographic', 'manhua', 'hiatus', 2000, '5', '["Ecchi"]', '["Anthology","Real"]', NULL, 9, NULL);
            """;
        cmd.ExecuteNonQuery();
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

    private EmbeddingStore Store()
    {
        var store = new EmbeddingStore(new EmbeddingOptions(_dir, VectorPath, _dir, EmbeddingModelProfile.Base));
        store.EnsureSchema();
        return store;
    }

    private VectorIndexCache Cache(RecordingLogger? logger = null) =>
        new(new EmbeddingOptions(_dir, VectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = 4 }),
            new MangaBakaDumpOptions(DumpPath, _dir),
            (ILogger<VectorIndexCache>?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VectorIndexCache>.Instance);

    private void Seed()
    {
        var store = Store();
        store.UpsertBatch([
            (1L, "h", [1f, 0.5f, 0f, -0.25f]),
            (2L, "h", [0f, 1f, 0.75f, 0f]),
            (3L, "h", [0.1f, 0.2f, 0.3f, 0.4f]),
            (4L, "h", [-1f, 0f, 0f, 1f]),
        ]);
        store.UpsertTagsBatch([(1L, [1, 2, 3, 4]), (3L, [9, 8])]);
    }

    private static string Fingerprint(VectorIndex index)
    {
        var sb = new StringBuilder();
        for (var row = 0; row < index.Count; row++)
        {
            sb.Append(index.IdAt(row)).Append('|').Append(index.ScaleAt(row)).Append('|')
                .AppendJoin(',', index.VectorAt(row)).Append('|').Append(index.RatingAt(row)).Append('|')
                .Append(index.PopularityAt(row)).Append('|').Append(index.YearAt(row)).Append('|')
                .Append(index.StartDayAt(row)).Append('|').AppendJoin(',', index.GenresAt(row).ToArray()).Append('|')
                .AppendJoin(',', index.AuthorsAt(row).ToArray()).Append('|').AppendJoin(',', index.ArtistsAt(row).ToArray()).Append('|')
                .Append(Convert.ToHexString(index.TagsAt(row))).Append('|');
            foreach (var filters in new RecommendationFilters[]
            {
                new(Types: ["manhwa"]), new(Statuses: ["hiatus"]), new(MinChapters: 10), new(YearMin: 2000),
                new(Genres: ["Drama"]), new(ContentRatings: ["safe"]), new(ContentRatings: ["erotica", "pornographic"]),
            })
            {
                sb.Append(index.Matches(row, index.Plan(filters)) ? '1' : '0');
            }

            sb.Append('\n');
        }

        sb.AppendJoin(',', index.GenreVocabulary.Select(kv => $"{kv.Key}={kv.Value}"));
        foreach (var name in new[] { "Miura", "miura", "Other", "Artist", "Real", "Anthology", "Nobody" })
        {
            sb.Append('|').Append(name).Append(index.TryGetAuthorId(name, out var id) ? id : -9);
        }

        return sb.ToString();
    }

    [Fact]
    public async Task A_fresh_cache_reads_the_snapshot_and_gets_the_index_a_build_would()
    {
        Seed();
        var first = Cache();
        var built = (await first.GetAsync())!;
        await first.SnapshotSavedForTest;
        Assert.True(File.Exists(first.SnapshotPath));

        var logger = new RecordingLogger();
        var reloaded = (await Cache(logger).GetAsync())!;

        Assert.Contains(logger.Messages, m => m.Contains("from its snapshot"));
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("Built the search vector index"));
        Assert.Equal(Fingerprint(built), Fingerprint(reloaded));
    }

    [Fact]
    public async Task A_write_to_the_vectors_makes_the_snapshot_stale_even_without_an_invalidate()
    {
        Seed();
        var first = Cache();
        await first.GetAsync();
        await first.SnapshotSavedForTest;

        Store().UpsertBatch([(1L, "h2", [0f, 0f, 0f, 1f])]);
        var logger = new RecordingLogger();
        var rebuilt = (await Cache(logger).GetAsync())!;

        Assert.DoesNotContain(logger.Messages, m => m.Contains("from its snapshot"));
        Assert.True(rebuilt.TryGetRow(1, out var row));
        Assert.Equal(0, rebuilt.VectorAt(row)[0], 3);
    }

    [Fact]
    public async Task A_new_dump_makes_the_snapshot_stale()
    {
        Seed();
        var first = Cache();
        await first.GetAsync();
        await first.SnapshotSavedForTest;

        using (var conn = new SqliteConnection($"Data Source={DumpPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE series SET popularity_global_current = 77 WHERE id = 1";
            cmd.ExecuteNonQuery();
        }

        File.SetLastWriteTimeUtc(DumpPath, DateTime.UtcNow.AddMinutes(1));
        var reloaded = (await Cache().GetAsync())!;

        Assert.True(reloaded.TryGetRow(1, out var row));
        Assert.Equal(77, reloaded.PopularityAt(row));
    }

    [Fact]
    public async Task Invalidate_deletes_the_snapshot()
    {
        Seed();
        var cache = Cache();
        await cache.GetAsync();
        await cache.SnapshotSavedForTest;

        cache.Invalidate();

        Assert.False(File.Exists(cache.SnapshotPath));
    }

    [Fact]
    public async Task A_damaged_snapshot_is_ignored_and_rebuilt_over()
    {
        Seed();
        var first = Cache();
        var built = (await first.GetAsync())!;
        await first.SnapshotSavedForTest;

        var bytes = File.ReadAllBytes(first.SnapshotPath);
        File.WriteAllBytes(first.SnapshotPath, bytes[..(bytes.Length / 2)]);

        var cache = Cache();
        var rebuilt = (await cache.GetAsync())!;
        await cache.SnapshotSavedForTest;

        Assert.Equal(Fingerprint(built), Fingerprint(rebuilt));
        Assert.Equal(bytes.Length, new FileInfo(cache.SnapshotPath).Length);
    }

    private sealed class RecordingLogger : ILogger<VectorIndexCache>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
