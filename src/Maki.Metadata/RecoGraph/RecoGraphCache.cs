using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.RecoGraph;

/// <summary>
/// Owns the process-wide <see cref="PairGraphIndex"/>: loads it on first use from
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
    private volatile PairGraphIndex? _graph;

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

            // No floor on how small a graph may be. Judging an artifact too small to be real is the
            // installer's job — it is the only thing that knows a download was meant to be complete,
            // and it has the manifest's own row count to check against. A file that got here by hand
            // is loaded as-is, the same as every other file a user drops into the config directory.
            var pairs = ReadPairs(conn, ct);
            if (pairs.Count == 0)
            {
                return null;
            }

            var graph = PairGraphBuilder.Build(pairs, ReadGeneratedAt(conn));
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

    /// <summary>
    /// Reads the pair table, putting both providers' votes on a common scale before combining them.
    ///
    /// <para>
    /// <b>They cannot simply be added.</b> AniList's number is a net upvote score over a much larger
    /// population and reaches into the thousands; MAL's is a count of users who submitted the
    /// recommendation and tops out in the dozens. Measured on the same titles, Berserk to Vinland
    /// Saga is 3,200 on AniList and 29 on MAL. Summing those raw makes MAL arithmetically invisible
    /// wherever AniList already has the pair — <c>log1p(3200)</c> and <c>log1p(3243)</c> differ by
    /// a tenth of a percent — so the second opinion this whole provider exists to give would be
    /// silently discarded.
    /// </para>
    ///
    /// <para>
    /// So each provider is divided by its own 90th percentile, taken across this artifact rather
    /// than hardcoded, which makes "a well-supported pair" mean the same thing on both sides
    /// whatever the populations do next. A high percentile rather than the median because the
    /// median is 1-2 votes on both and would calibrate on noise; a percentile rather than the
    /// maximum because the maximum is one outlier per provider.
    /// </para>
    /// </summary>
    private static List<(long A, long B, float Votes)> ReadPairs(SqliteConnection conn, CancellationToken ct)
    {
        var raw = new List<(long A, long B, int AniList, int Mal)>(120_000);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT a_id, b_id, anilist_votes, mal_votes FROM pair";
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

                raw.Add((
                    a,
                    b,
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3)));
            }
        }

        var aniListScale = Percentile90([.. raw.Where(r => r.AniList > 0).Select(r => r.AniList)]);
        var malScale = Percentile90([.. raw.Where(r => r.Mal > 0).Select(r => r.Mal)]);

        // Votes stay integers on AniList's scale rather than becoming floats, so RecoGraphTuning's
        // MinVotes floor keeps meaning what it says. An artifact from only one provider leaves the
        // other's scale at zero and its term drops out, which is exactly today's state.
        var ratio = aniListScale > 0 && malScale > 0 ? (double)aniListScale / malScale : 0;

        var pairs = new List<(long, long, float)>(raw.Count);
        foreach (var (a, b, aniList, mal) in raw)
        {
            pairs.Add((a, b, aniList + (float)Math.Round(mal * ratio)));
        }

        return pairs;
    }

    /// <summary>The 90th percentile of a sample, or 0 when there is nothing to take it of.</summary>
    private static int Percentile90(int[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        Array.Sort(values);
        return values[Math.Min(values.Length - 1, (int)(values.Length * 0.9))];
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
}
