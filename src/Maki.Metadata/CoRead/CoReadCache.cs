using System.Globalization;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.CoRead;

/// <summary>
/// Where the co-read edge database lives. Constructed from AppPaths in the API host
/// (Maki.Metadata cannot reference Maki.Api).
/// <para>
/// This is <c>coread-edges.db</c>, the folded artifact, never <c>coread-graph.db</c> — that second
/// file is <c>distribution/fetch-coread-graph.cs</c>'s working state and holds <c>user_entry</c>,
/// which is per-user reading data and must never leave the machine that fetched it.
/// </para>
/// </summary>
public record CoReadOptions(string DatabasePath, string StagingDirectory);

/// <summary>
/// Owns the process-wide co-read <see cref="PairGraphIndex"/>: loads it on first use from
/// <c>coread-edges.db</c> and hands the same instance to every request.
///
/// <para>
/// Deliberately mirrors <see cref="RecoGraphCache"/>, including the swap semantics, because the
/// file arrives the same way — a downloaded artifact replaced under a running process.
/// </para>
///
/// <para>
/// <b>An absent file is normal, not an error.</b> <see cref="GetAsync"/> returns null and every
/// caller treats that as "no graph", leaving recommendations exactly as they were before this
/// channel existed.
/// </para>
/// </summary>
public sealed class CoReadCache(CoReadOptions options, ILogger<CoReadCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile PairGraphIndex? _graph;

    /// <summary>Drops the loaded graph so the next request reloads it. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        _graph = null;
        logger.LogDebug("Co-read graph invalidated");
    }

    /// <summary>
    /// Replaces the edge database with <paramref name="stagedPath"/> and drops the loaded graph.
    /// Runs under the load lock so a swap can never race a load midway through reading the old
    /// file. The WAL sidecars belong to the file being replaced, so they go with it — leaving them
    /// would let SQLite reconstruct pages of the <em>previous</em> database over the new one.
    /// </summary>
    public async Task SwapDatabaseAsync(string stagedPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _graph = null;
            SqliteConnection.ClearAllPools();

            foreach (var sidecar in new[] { options.DatabasePath + "-wal", options.DatabasePath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Move(stagedPath, options.DatabasePath, overwrite: true);
            logger.LogInformation("Swapped in a new co-read graph at {Path}", options.DatabasePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The graph, loading it if needed. Null when there is nothing to read: no file, an empty one,
    /// or one that fails to open.
    /// </summary>
    public async Task<PairGraphIndex?> GetAsync(CancellationToken ct = default)
    {
        if (_graph is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_graph is { } raced)
            {
                return raced;
            }

            if (!File.Exists(options.DatabasePath))
            {
                return null;
            }

            _graph = await Task.Run(() => Load(ct), ct);
            return _graph;
        }
        finally
        {
            _lock.Release();
        }
    }

    private PairGraphIndex? Load(CancellationToken ct)
    {
        var started = DateTime.UtcNow;

        try
        {
            using var conn = new SqliteConnection(
                $"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();

            // No floor on how small a graph may be, for the same reason RecoGraphCache has none:
            // judging an artifact too small to be real is the installer's job, since it is the only
            // thing that knows a download was meant to be complete.
            var pairs = ReadPairs(conn, ct);
            if (pairs.Count == 0)
            {
                return null;
            }

            var graph = PairGraphBuilder.Build(pairs, ReadGeneratedAt(conn));
            logger.LogInformation(
                "Loaded co-read graph: {Series} series, {Pairs} pairs in {Elapsed:F0}ms",
                graph.Count, pairs.Count, (DateTime.UtcNow - started).TotalMilliseconds);
            return graph;
        }
        catch (SqliteException ex)
        {
            // A corrupt or half-written artifact must not take recommendations down with it: the
            // whole channel is an optional bonus on top of a working content-based recommender.
            logger.LogWarning(ex, "Could not read the co-read graph at {Path}", options.DatabasePath);
            return null;
        }
    }

    /// <summary>
    /// Reads the pair table. Simpler than <see cref="RecoGraphCache"/>'s equivalent because there
    /// is one provider and one already-comparable unit: the build has divided every co-occurrence
    /// by both endpoints' popularity, so a strength means the same thing across the whole file and
    /// needs no rescaling. <c>support</c> is deliberately not carried — it is a build-time filter
    /// (<c>minSupport</c>) already applied to every row that reached the artifact, and a second
    /// array would double the index for a floor nothing has measured a use for.
    /// </summary>
    private static List<(long A, long B, float Weight)> ReadPairs(SqliteConnection conn, CancellationToken ct)
    {
        var pairs = new List<(long, long, float)>(1_200_000);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT a_id, b_id, strength FROM pair";
        cmd.CommandTimeout = 120;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var a = reader.GetInt64(0);
            var b = reader.GetInt64(1);
            if (a == b)
            {
                continue; // a series cannot corroborate itself
            }

            var strength = reader.IsDBNull(2) ? 0 : reader.GetFloat(2);
            if (strength <= 0 || !float.IsFinite(strength))
            {
                continue;
            }

            pairs.Add((a, b, strength));
        }

        return pairs;
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
