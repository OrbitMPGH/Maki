using Maki.Core;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.ReaderCohorts;

/// <summary>
/// Owns the process-wide <see cref="ReaderCohortIndex"/>: loads it on first use from
/// <c>reader-cohorts.db</c> and hands the same instance to every request.
///
/// <para>
/// Deliberately shaped like <see cref="CoRead.CoReadCache"/> rather than like the behavioural
/// vectors, which live inside <c>VectorIndexCache</c> and so make installing them invalidate the
/// whole vector index. Nothing here is scanned per catalogue row, so this file can be swapped under
/// a running process at a cost of one reload.
/// </para>
///
/// <para>
/// <b>An absent file is normal, not an error.</b> <see cref="GetAsync"/> returns null; the hint
/// does not render, the rail returns null, and the taste page falls back to the popularity proxy
/// it used before this artifact existed.
/// </para>
/// </summary>
public sealed class ReaderCohortCache(ReaderCohortOptions options, ILogger<ReaderCohortCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile ReaderCohortIndex? _index;
    private readonly IdleStamp _idle = new();

    /// <summary>Whether the artifact is currently in memory, for the memory diagnostics.</summary>
    public bool IsLoaded => _index is not null;

    /// <summary>How long since anything last read it. Meaningless while unloaded.</summary>
    public TimeSpan IdleFor => _idle.Idle;

    /// <summary>Drops the loaded index so the next request reloads it. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        _index = null;
        logger.LogDebug("Reader cohorts invalidated");
    }

    /// <summary>
    /// Replaces the database with <paramref name="stagedPath"/> and drops the loaded index. Runs
    /// under the load lock so a swap can never race a load midway through reading the old file. The
    /// WAL sidecars belong to the file being replaced, so they go with it — leaving them would let
    /// SQLite reconstruct pages of the <em>previous</em> database over the new one.
    /// </summary>
    public async Task SwapDatabaseAsync(string stagedPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _index = null;
            SqliteConnection.ClearAllPools();

            foreach (var sidecar in new[] { options.DatabasePath + "-wal", options.DatabasePath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Move(stagedPath, options.DatabasePath, overwrite: true);
            logger.LogInformation("Swapped in new reader cohorts at {Path}", options.DatabasePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Drops the loaded artifact when nothing has read it for <paramref name="idleFor"/>, and
    /// reports whether it did.
    ///
    /// <para>
    /// No lock, unlike the embedder's equivalent. What is handed out here is an immutable index and
    /// a caller holds its own reference to it, so clearing the field only makes it collectable once
    /// the last reader is finished with it - there is no native handle to dispose out from under a
    /// pass. A request that arrives mid-unload either sees the old index or rebuilds, and both are
    /// correct.
    /// </para>
    /// </summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        var idle = _idle.Idle;
        if (_index is null || idle < idleFor)
        {
            return false;
        }

        _index = null;
        logger.LogInformation(
            "Unloaded the reader cohorts after {Minutes:F0} idle minute(s); it reloads on next use",
            idle.TotalMinutes);
        return true;
    }

    /// <summary>
    /// The index, loading it if needed. Null when there is nothing to read: no file, an empty one,
    /// or one that fails to open.
    /// </summary>
    public async Task<ReaderCohortIndex?> GetAsync(CancellationToken ct = default)
    {
        if (_index is { } cached)
        {
            _idle.Touch();
            return cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_index is { } raced)
            {
                _idle.Touch();
                return raced;
            }

            if (!File.Exists(options.DatabasePath))
            {
                return null;
            }

            _index = await Task.Run(() => Load(ct), ct);
            _idle.Touch();
            return _index;
        }
        finally
        {
            _lock.Release();
        }
    }

    private ReaderCohortIndex? Load(CancellationToken ct)
    {
        var started = DateTime.UtcNow;

        try
        {
            using var conn = new SqliteConnection(
                $"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();

            var cohortReaders = ReadCohorts(conn);
            if (cohortReaders.Length == 0)
            {
                return null;
            }

            var globalRows = ReadGlobal(conn, ct);
            if (globalRows.Count == 0)
            {
                return null;
            }

            var cohortRows = ReadCohortItems(conn, ct);
            var index = ReaderCohortIndexBuilder.Build(
                globalRows, cohortRows, cohortReaders, ReadInt(conn, "completionP99"), ReadGeneratedAt(conn));

            logger.LogInformation(
                "Loaded reader cohorts: {Cohorts} cohorts over {Readers} readers, {Series} series, "
                + "{Rows} cohort rows in {Elapsed:F0}ms",
                index.CohortCount, index.TotalReaders, index.Count, index.EntryCount,
                (DateTime.UtcNow - started).TotalMilliseconds);
            return index;
        }
        catch (Exception ex) when (ex is SqliteException or ArgumentOutOfRangeException)
        {
            // A corrupt or half-written artifact must not take the Discover page down with it:
            // every surface reading this treats null as "no cohorts" and renders as it did before
            // the feature existed.
            logger.LogWarning(ex, "Could not read reader cohorts at {Path}", options.DatabasePath);
            return null;
        }
    }

    private static int[] ReadCohorts(SqliteConnection conn)
    {
        var readers = new List<int>(32);
        using var cmd = conn.CreateCommand();
        // Ordered so the list index IS the cohort id, which is what every row's `cohort` column
        // refers to and what the serving side indexes its weight array by.
        cmd.CommandText = "SELECT cohort, readers FROM cohort ORDER BY cohort";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt32(0) != readers.Count)
            {
                // A gap means the file was written by something that renumbered cohorts, and every
                // row's cohort column would then point at the wrong group.
                return [];
            }

            readers.Add(reader.GetInt32(1));
        }

        return [.. readers];
    }

    private static List<(long Id, int Completions, int Raters, float? Mean)> ReadGlobal(
        SqliteConnection conn, CancellationToken ct)
    {
        var rows = new List<(long, int, int, float?)>(80_000);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, completions, raters, mean FROM item_global";
        cmd.CommandTimeout = 120;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rows.Add((
                reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : (float)reader.GetDouble(3)));
        }

        return rows;
    }

    private static List<(long Id, int Cohort, int Completions, int Raters, float? Mean)> ReadCohortItems(
        SqliteConnection conn, CancellationToken ct)
    {
        var rows = new List<(long, int, int, int, float?)>(200_000);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, cohort, completions, raters, mean FROM cohort_item";
        cmd.CommandTimeout = 120;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rows.Add((
                reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : (float)reader.GetDouble(4)));
        }

        return rows;
    }

    private static int ReadInt(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() is string raw
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static DateTime? ReadGeneratedAt(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'generatedAt'";
            var value = cmd.ExecuteScalar() as string;
            return DateTime.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
