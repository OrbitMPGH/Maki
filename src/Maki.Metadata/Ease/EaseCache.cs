using System.Globalization;
using Maki.Core;
using Maki.Core.Io;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Ease;

/// <summary>
/// Where the "readers who finished" weight table lives, and where its download is staged.
/// Constructed from AppPaths in the API host.
/// </summary>
public record EaseOptions(string DatabasePath, string? StagingDirectory = null);

/// <summary>
/// Owns the process-wide <see cref="EaseTable"/>: loads it on first use from <c>ease-table.db</c>
/// and hands the same instance to every request.
///
/// <para>
/// Mirrors <see cref="CoRead.CoReadCache"/> on purpose, because the file arrives the same way: a
/// downloaded artifact replaced whole under a running process. That includes the idle unload, the
/// swap under the load lock and the memory-mapped snapshot, which turns a ~1 s, 24 MB rebuild from
/// SQLite into a near-instant reload whose pages live in the file rather than on the heap.
/// </para>
///
/// <para>
/// <b>An absent file is normal, not an error.</b> <see cref="GetAsync"/> returns null and
/// <c>SemanticRecommender</c> then behaves exactly as it does without the channel.
/// </para>
/// </summary>
public sealed class EaseCache(EaseOptions options, ILogger<EaseCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile EaseTable? _table;
    private readonly IdleStamp _idle = new();
    private readonly LoadFailureMemo _failed = new();
    private readonly SharedBuild<EaseTable> _loads = new();
    private volatile TaskCompletionSource? _published;
    private int _generation;

    /// <summary>Moves on every swap or invalidation, so a recommendation pool built on the old table is not served.</summary>
    public int Generation => Volatile.Read(ref _generation);

    private const ulong SnapshotMagic = 0x3145534145434B4D; // "MKCEASE1"

    public string DatabasePath => options.DatabasePath;

    /// <summary>Whether the table is currently in memory, for the memory diagnostics.</summary>
    public bool IsLoaded => _table is not null;

    /// <summary>How long since anything last read it. Meaningless while unloaded.</summary>
    public TimeSpan IdleFor => _idle.Idle;

    /// <summary>
    /// The loaded table's CSR arrays, kept between unloads. Keyed by the artifact's write time and
    /// length, since the artifact is only ever replaced whole.
    /// </summary>
    internal string SnapshotPath => Path.ChangeExtension(options.DatabasePath, ".weights");

    /// <summary>Test hook: the latest background snapshot write.</summary>
    internal Task SnapshotSavedForTest { get; private set; } = Task.CompletedTask;

    /// <summary>Drops the loaded table so the next request reloads it. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        _table = null;
        _failed.Clear();
        SnapshotFile.TryDelete(SnapshotPath);
        Interlocked.Increment(ref _generation);
        logger.LogDebug("Readers-also-finished table invalidated");
    }

    /// <summary>
    /// Replaces the table file with <paramref name="stagedPath"/> and drops the loaded table, under
    /// the load lock so a swap never races a load reading the old file.
    /// </summary>
    public async Task SwapDatabaseAsync(string stagedPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await _loads.DrainAsync();
            _table = null;
            SnapshotFile.TryDelete(SnapshotPath);
            SqliteConnection.ClearAllPools();

            foreach (var sidecar in new[] { options.DatabasePath + "-wal", options.DatabasePath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Move(stagedPath, options.DatabasePath, overwrite: true);
            _failed.Clear();
            Interlocked.Increment(ref _generation);
            logger.LogInformation("Swapped in a new readers-also-finished table at {Path}", options.DatabasePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Drops the loaded table when nothing has read it for <paramref name="idleFor"/>. No lock, for
    /// the reason <see cref="CoRead.CoReadCache.ReleaseIfIdle"/> gives: the table is immutable and a
    /// caller holds its own reference.
    /// </summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        var idle = _idle.Idle;
        if (_table is null || idle < idleFor)
        {
            return false;
        }

        _table = null;
        logger.LogInformation(
            "Unloaded the readers-also-finished table after {Minutes:F0} idle minute(s); it reloads on next use",
            idle.TotalMinutes);
        return true;
    }

    /// <summary>
    /// The table, loading it if needed. Null when there is nothing to read: no file, an empty one,
    /// or one that fails to open.
    /// </summary>
    public async Task<EaseTable?> GetAsync(CancellationToken ct = default)
    {
        if (_table is { } cached)
        {
            _idle.Touch();
            return cached;
        }

        Task<EaseTable?> load;
        await _lock.WaitAsync(ct);
        try
        {
            if (_table is { } raced)
            {
                _idle.Touch();
                return raced;
            }

            if (!File.Exists(options.DatabasePath) || _failed.ShouldSkip(options.DatabasePath))
            {
                return null;
            }

            load = _loads.Join(() =>
            {
                var observed = _failed.Observe(options.DatabasePath);
                try
                {
                    var loaded = Load();
                    _table = loaded;
                    _published?.TrySetResult();
                    _idle.Touch();
                    if (loaded is null)
                    {
                        _failed.Record(observed);
                    }
                    else
                    {
                        _failed.Clear();
                    }

                    return loaded;
                }
                catch (Exception ex)
                {
                    _failed.Record(observed);
                    logger.LogWarning(ex, "Loading the readers-also-finished table failed");
                    throw;
                }
            });
        }
        finally
        {
            _lock.Release();
        }

        return await load.WaitAsync(ct);
    }

    private EaseTable? Load()
    {
        var started = DateTime.UtcNow;
        var file = new FileInfo(options.DatabasePath);
        var key = $"{SnapshotFile.BuildIdentity}|{file.LastWriteTimeUtc.Ticks}:{file.Length}";
        try
        {
            if (SnapshotFile.Load(SnapshotPath, SnapshotMagic, key, EaseTable.ReadFrom) is { } saved)
            {
                logger.LogInformation(
                    "Loaded readers-also-finished table from its snapshot: {Seeds} seeds, {Weights} weights in {Elapsed:F0}ms",
                    saved.Count, saved.WeightCount, (DateTime.UtcNow - started).TotalMilliseconds);
                return saved;
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Ignoring an unreadable readers-also-finished snapshot; reloading");
        }

        var loaded = LoadFromDatabase(started);
        if (loaded is not null)
        {
            // Trade the heap copy for one reading the snapshot in place once it is written.
            var published = _published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SnapshotSavedForTest = Task.Run(async () =>
            {
                try
                {
                    SnapshotFile.Save(SnapshotPath, SnapshotMagic, key, loaded.WriteTo);
                    await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (SnapshotFile.Load(SnapshotPath, SnapshotMagic, key, EaseTable.ReadFrom) is { IsMapped: true } mapped)
                    {
                        Interlocked.CompareExchange(ref _table, mapped, loaded);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Could not save the readers-also-finished snapshot");
                }
            });
        }

        return loaded;
    }

    private EaseTable? LoadFromDatabase(DateTime started)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var table = EaseTable.Build(ReadWeights(conn), ReadGeneratedAt(conn));
            if (table.Count == 0)
            {
                return null;
            }

            logger.LogInformation(
                "Loaded readers-also-finished table: {Seeds} seeds, {Weights} weights in {Elapsed:F0}ms",
                table.Count, table.WeightCount, (DateTime.UtcNow - started).TotalMilliseconds);
            return table;
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not read the readers-also-finished table at {Path}", options.DatabasePath);
            return null;
        }
        finally
        {
            // Every row was read; the snapshot serves reloads until the artifact is replaced.
            PageCache.DropAfterScan(options.DatabasePath);
        }
    }

    private static IEnumerable<(long Seed, long Target, float Weight)> ReadWeights(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seed_id, item_id, weight FROM weight";
        cmd.CommandTimeout = 120;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetFloat(2));
        }
    }

    private static DateTime? ReadGeneratedAt(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'generatedAt'";
            return DateTime.TryParse(
                cmd.ExecuteScalar() as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
