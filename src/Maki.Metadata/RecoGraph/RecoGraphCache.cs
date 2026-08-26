using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.RecoGraph;

/// <summary>
/// Owns the process-wide <see cref="RecoGraphIndex"/>: loads it on first use from
/// <c>reco-edges.db</c> and hands the same instance to every request.
///
/// <para>
/// Deliberately mirrors <see cref="Embedding.VectorIndexCache"/>, including the swap semantics,
/// because the file arrives the same way — a downloaded artifact replaced under a running process.
/// </para>
///
/// <para>
/// <b>An absent file is normal, not an error.</b> <see cref="GetAsync"/> returns null and every
/// caller treats that as "no graph", leaving recommendations exactly as they were before this
/// feature existed. That is what lets the channel ship ahead of anything being published.
/// </para>
/// </summary>
public sealed class RecoGraphCache(RecoGraphOptions options, ILogger<RecoGraphCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile RecoGraphIndex? _graph;

    /// <summary>Drops the loaded graph so the next request reloads it. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        _graph = null;
        logger.LogDebug("Co-recommendation graph invalidated");
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
            logger.LogInformation("Swapped in a new co-recommendation graph at {Path}", options.DatabasePath);
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
    public async Task<RecoGraphIndex?> GetAsync(CancellationToken ct = default)
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

    private RecoGraphIndex? Load(CancellationToken ct)
    {
        var started = DateTime.UtcNow;

        try
        {
            using var conn = new SqliteConnection(
                $"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();

            // No floor on how small a graph may be. Judging an artifact too small to be real is the
            // installer's job — it is the only thing that knows a download was meant to be complete,
            // and it has the manifest's own row count to check against. A file that got here by hand
            // is loaded as-is, the same as every other file a user drops into the config directory.
            var pairs = ReadPairs(conn, ct);
            if (pairs.Count == 0)
            {
                return null;
            }

            var graph = BuildCsr(pairs, ReadGeneratedAt(conn));
            logger.LogInformation(
                "Loaded co-recommendation graph: {Series} series, {Pairs} pairs in {Elapsed:F0}ms",
                graph.Count, pairs.Count, (DateTime.UtcNow - started).TotalMilliseconds);
            return graph;
        }
        catch (SqliteException ex)
        {
            // A corrupt or half-written artifact must not take recommendations down with it: the
            // whole channel is an optional bonus on top of a working content-based recommender.
            logger.LogWarning(ex, "Could not read the co-recommendation graph at {Path}", options.DatabasePath);
            return null;
        }
    }

    private static List<(long A, long B, int Votes)> ReadPairs(SqliteConnection conn, CancellationToken ct)
    {
        var pairs = new List<(long, long, int)>(120_000);
        using var cmd = conn.CreateCommand();

        // Providers are summed rather than kept apart. They measure the same thing in different
        // populations, and a consumer that wanted to weigh one against the other would need a
        // calibration nobody has: AniList ratings and MAL vote counts are not the same unit, but
        // both are "how many people said so", and the log compression downstream flattens what
        // difference in scale remains.
        cmd.CommandText = "SELECT a_id, b_id, anilist_votes + mal_votes FROM pair";
        cmd.CommandTimeout = 120;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var a = reader.GetInt64(0);
            var b = reader.GetInt64(1);
            if (a == b)
            {
                continue; // two remote entries folded onto one MangaBaka row
            }

            pairs.Add((a, b, reader.IsDBNull(2) ? 0 : reader.GetInt32(2)));
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
            // Artifacts exported before the meta table existed are still perfectly good graphs.
            return null;
        }
    }

    /// <summary>
    /// Folds unordered pairs into a CSR adjacency, materializing both directions. Two counting
    /// passes rather than a dictionary of lists: the node count is six figures and the per-node
    /// degree is a handful, so the list-per-node form would allocate more headers than data.
    /// </summary>
    private static RecoGraphIndex BuildCsr(List<(long A, long B, int Votes)> pairs, DateTime? generatedAt)
    {
        var idSet = new HashSet<long>(pairs.Count);
        foreach (var (a, b, _) in pairs)
        {
            idSet.Add(a);
            idSet.Add(b);
        }

        var ids = idSet.ToArray();
        Array.Sort(ids);

        var nodeById = new Dictionary<long, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            nodeById[ids[i]] = i;
        }

        // Pass 1: degrees, accumulated into the offset array shifted by one so the prefix sum
        // below turns it into the offsets directly.
        var offsets = new int[ids.Length + 1];
        foreach (var (a, b, _) in pairs)
        {
            offsets[nodeById[a] + 1]++;
            offsets[nodeById[b] + 1]++;
        }

        for (var i = 0; i < ids.Length; i++)
        {
            offsets[i + 1] += offsets[i];
        }

        // Pass 2: fill. `cursor` walks each node's slice as entries land in it.
        var neighbours = new int[pairs.Count * 2];
        var votes = new int[pairs.Count * 2];
        var cursor = new int[ids.Length];

        foreach (var (a, b, v) in pairs)
        {
            var na = nodeById[a];
            var nb = nodeById[b];

            var slotA = offsets[na] + cursor[na]++;
            neighbours[slotA] = nb;
            votes[slotA] = v;

            var slotB = offsets[nb] + cursor[nb]++;
            neighbours[slotB] = na;
            votes[slotB] = v;
        }

        return new RecoGraphIndex(ids, offsets, neighbours, votes, generatedAt);
    }
}
