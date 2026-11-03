using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>The co-read graph reloads from a snapshot of its folded arrays, keyed to the artifact file.</summary>
public class CoReadSnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "maki-coreadsnap-" + Guid.NewGuid().ToString("N"))).FullName;

    private string DbPath => Path.Combine(_dir, "coread-edges.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A background save or a mapping still open; only leaves a temp folder behind.
        }
    }

    private void Write(params (long A, long B, float Strength)[] pairs)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE pair (a_id INTEGER NOT NULL, b_id INTEGER NOT NULL, support INTEGER NOT NULL DEFAULT 3,
                strength REAL NOT NULL, PRIMARY KEY (a_id, b_id)) WITHOUT ROWID;
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta VALUES ('generated_at', '2026-10-01T00:00:00Z');
            """ + $"INSERT INTO pair (a_id, b_id, strength) VALUES {string.Join(",", pairs.Select(p => $"({p.A}, {p.B}, {p.Strength.ToString(System.Globalization.CultureInfo.InvariantCulture)})"))};";
        cmd.ExecuteNonQuery();
    }

    private CoReadCache Cache(RecordingLogger? logger = null) =>
        new(new CoReadOptions(DbPath, _dir), (ILogger<CoReadCache>?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CoReadCache>.Instance);

    private static string Describe(PairGraphIndex graph)
    {
        var lines = new List<string> { $"{graph.Count}/{graph.EdgeCount}/{graph.GeneratedAt:O}" };
        for (var node = 0; node < graph.Count; node++)
        {
            var edges = graph.EdgesAt(node);
            var parts = new List<string>();
            for (var i = 0; i < edges.Length; i++)
            {
                parts.Add($"{graph.IdAt(edges.Neighbour(i))}={edges.Weight(i)}");
            }

            lines.Add($"{graph.IdAt(node)}:" + string.Join(",", parts));
        }

        return string.Join("\n", lines);
    }

    [Fact]
    public async Task A_fresh_cache_reads_the_snapshot_and_gets_the_same_graph()
    {
        Write((1, 2, 0.5f), (1, 3, 0.25f), (900_000, 2, 1.5f), (4, 5, 0.125f));
        var first = Cache();
        var loaded = (await first.GetAsync())!;
        await first.SnapshotSavedForTest;
        Assert.True(File.Exists(first.SnapshotPath));

        var logger = new RecordingLogger();
        var reloaded = (await Cache(logger).GetAsync())!;

        Assert.Contains(logger.Messages, m => m.Contains("from its snapshot"));
        Assert.Equal(Describe(loaded), Describe(reloaded));
    }

    [Fact]
    public async Task A_loaded_graph_is_swapped_for_the_mapped_snapshot()
    {
        Write((1, 2, 0.5f), (1, 3, 0.25f), (4, 5, 0.125f));
        var cache = Cache();
        var loaded = (await cache.GetAsync())!;
        await cache.SnapshotSavedForTest;

        var published = (await cache.GetAsync())!;

        Assert.False(loaded.IsMapped);
        Assert.True(published.IsMapped);
        Assert.Equal(Describe(loaded), Describe(published));
    }

    [Fact]
    public async Task A_replaced_artifact_is_read_instead_of_the_old_snapshot()
    {
        Write((1, 2, 0.5f));
        var first = Cache();
        await first.GetAsync();
        await first.SnapshotSavedForTest;

        Write((7, 8, 0.75f), (7, 9, 0.5f));
        var second = Cache();
        var reloaded = (await second.GetAsync())!;
        await second.SnapshotSavedForTest;

        Assert.True(reloaded.TryGetNode(9, out _));
        Assert.False(reloaded.TryGetNode(1, out _));
    }

    [Fact]
    public async Task Invalidate_and_swap_delete_the_snapshot()
    {
        Write((1, 2, 0.5f));
        var cache = Cache();
        await cache.GetAsync();
        await cache.SnapshotSavedForTest;
        cache.Invalidate();
        Assert.False(File.Exists(cache.SnapshotPath));

        await cache.GetAsync();
        await cache.SnapshotSavedForTest;
        var staged = Path.Combine(_dir, "staged.db");
        File.Copy(DbPath, staged);
        await cache.SwapDatabaseAsync(staged);
        Assert.False(File.Exists(cache.SnapshotPath));
    }

    private sealed class RecordingLogger : ILogger<CoReadCache>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
