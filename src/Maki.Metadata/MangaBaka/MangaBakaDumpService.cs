using System.Globalization;
using System.Security.Cryptography;
using Maki.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Maintains the local copy of the MangaBaka database dump: nightly snapshots published
/// at api.mangabaka.org/v1/database/ (~350 MB zst, ~3 GB unpacked). Downloads only when
/// the published SHA1 changes, builds an FTS5 title index, and atomically swaps the file
/// into place so readers never see a half-written database.
/// </summary>
public class MangaBakaDumpService(
    IHttpClientFactory httpClientFactory,
    MangaBakaDumpOptions options,
    IAppSettings settings,
    ILogger<MangaBakaDumpService> logger)
{
    public const string HttpClientName = "mangabaka-dump";
    public const string SearchTableName = "maki_search";

    private const string StandardDumpPath = "v1/database/series.sqlite.zst";

    /// <summary>
    /// The "full" dump keeps each source's flattened response columns (notably the MangaUpdates
    /// description) that the standard dump strips to save size. Larger (~4.6 GB vs ~3.5 GB), so
    /// it's opt-in and only useful on a machine that builds the embedding index locally.
    /// </summary>
    private const string FullDumpPath = "v1/database/series.full.sqlite.zst";

    private async Task<string> DumpPathAsync(CancellationToken ct) =>
        string.Equals(await settings.GetAsync(SettingKeys.MangaBakaUseFullDump, ct), "true", StringComparison.OrdinalIgnoreCase)
            ? FullDumpPath
            : StandardDumpPath;

    public record DumpStatus(bool Present, long? SizeBytes, DateTime? RefreshedAt);

    public async Task<DumpStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var info = new FileInfo(options.DatabasePath);
        DateTime? refreshedAt = DateTime.TryParse(
            await settings.GetAsync(SettingKeys.MangaBakaDumpRefreshedAt, ct),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        return new DumpStatus(info.Exists, info.Exists ? info.Length : null, refreshedAt);
    }

    /// <summary>Downloads and installs the dump if its checksum changed; returns true when a new dump was installed.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var dumpPath = await DumpPathAsync(ct);

        // Published as "<hex sha1>  <filename>" over the compressed file.
        var sha1Line = await client.GetStringAsync(dumpPath + ".sha1", ct);
        var expectedSha1 = sha1Line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        var installedSha1 = await settings.GetAsync(SettingKeys.MangaBakaDumpSha1, ct);
        if (string.Equals(expectedSha1, installedSha1, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(options.DatabasePath))
        {
            logger.LogDebug("MangaBaka dump unchanged ({Sha1}); skipping download", expectedSha1);
            return false;
        }

        Directory.CreateDirectory(options.StagingDirectory);
        var stagingPath = Path.Combine(options.StagingDirectory, "mangabaka.db.partial");

        try
        {
            var actualSha1 = await DownloadAndDecompressAsync(client, dumpPath, stagingPath, ct);
            if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"MangaBaka dump checksum mismatch: expected {expectedSha1}, got {actualSha1}");
            }

            PrepareStagedDatabase(stagingPath);
            await SwapIntoPlaceAsync(stagingPath, ct);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }

        await settings.SetAsync(SettingKeys.MangaBakaDumpSha1, expectedSha1, ct);
        await settings.SetAsync(SettingKeys.MangaBakaDumpRefreshedAt, DateTime.UtcNow.ToString("O"), ct);
        logger.LogInformation("MangaBaka local database installed at {Path} ({Sha1})", options.DatabasePath, expectedSha1);
        return true;
    }

    /// <summary>Streams the zst dump to disk, decompressing on the fly while hashing the compressed bytes.</summary>
    private async Task<string> DownloadAndDecompressAsync(
        HttpClient client, string dumpPath, string stagingPath, CancellationToken ct)
    {
        logger.LogInformation("Downloading MangaBaka database dump ({Dump})…", dumpPath);
        using var response = await client.GetAsync(dumpPath, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var hashing = new HashingReadStream(source, sha1);
        await using var decompressed = new DecompressionStream(hashing);
        await using (var output = File.Create(stagingPath))
        {
            await decompressed.CopyToAsync(output, ct);
        }

        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    /// <summary>Sanity-checks the staged database and builds the FTS5 index over all title variants.</summary>
    private void PrepareStagedDatabase(string stagingPath)
    {
        using var conn = new SqliteConnection($"Data Source={stagingPath};Pooling=False");
        conn.Open();

        var count = (long)Scalar(conn, "SELECT COUNT(*) FROM series")!;
        if (count < 1000)
        {
            throw new InvalidOperationException($"MangaBaka dump looks broken: only {count} series rows");
        }

        logger.LogInformation("Building MangaBaka search index over {Count} series…", count);
        BuildSearchIndex(conn);

        logger.LogInformation("Building MangaBaka browse indexes…");
        BuildBrowseIndexes(conn, logger);
    }

    /// <summary>
    /// The names of the indexes <see cref="BuildBrowseIndexes"/> creates. Presence of the last one
    /// is what <see cref="EnsureBrowseIndexesAsync"/> tests, so keep it last in the build order.
    /// </summary>
    private static readonly string[] BrowseIndexNames =
    [
        "ix_browse_pop", "ix_browse_trend", "ix_browse_new", "ix_browse_rating", "ix_browse_type",
        "ix_title_nocase",
    ];

    /// <summary>
    /// Indexes the columns the Discover rails filter and sort on. The dump ships with <b>no indexes
    /// at all</b>, so without these every rail is a full scan of ~558k rows across ~3.5 GB plus a
    /// sort: measured at 11s for the six-rail set, and far worse whenever the page cache is cold or
    /// the disk is busy, which is what makes the endpoint's tail latency unbounded.
    ///
    /// <para>
    /// Measured 17.25s to 0.43s over the six rails, a 40x improvement, with every rail reporting an
    /// index rather than a scan. Costs ~15s to build and ~13 MB.
    /// </para>
    ///
    /// <para>
    /// All are <b>partial</b> indexes over the rails' common quality gate (active, not a novel,
    /// rated, has a cover). That predicate is duplicated from <c>MangaBakaLocalStore.GetBrowseAsync</c>
    /// and must stay in step with it: SQLite will only use a partial index when the query's WHERE
    /// provably implies the index's, so a rail that drops one of these conditions silently falls back
    /// to a full scan rather than failing. The <c>title NOT LIKE</c> clause is deliberately left out
    /// - it excludes few rows and a LIKE in the predicate would stop the planner matching it.
    /// </para>
    /// </summary>
    /// <summary>
    /// The subset of <see cref="BrowseIndexNames"/> this dump has the columns for. A dump variant
    /// that drops a column simply gets fewer indexes; that is a slower rail, never a failed refresh.
    /// </summary>
    private static HashSet<string> BuildableIndexNames(SqliteConnection conn)
    {
        var present = ColumnsOf(conn);
        var buildable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, columns, _) in IndexDefinitions())
        {
            if (GateColumns.Concat(columns).All(present.Contains))
            {
                buildable.Add(name);
            }
        }

        return buildable;
    }

    private static HashSet<string> ColumnsOf(SqliteConnection conn)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(series)";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            present.Add(reader.GetString(1));
        }

        return present;
    }

    /// <summary>
    /// The rails' shared quality gate, duplicated from <c>MangaBakaLocalStore.GetBrowseAsync</c> and
    /// required to stay in step with it. SQLite only uses a partial index when the query's WHERE
    /// provably implies the index's, so a rail that drops one of these conditions silently falls
    /// back to a full scan rather than failing. The query's <c>title NOT LIKE 'unknown title%'</c>
    /// is deliberately absent here: it excludes few rows, and a LIKE in the predicate would stop the
    /// planner matching it.
    /// </summary>
    private const string BrowseGate =
        "state = 'active' AND type != 'novel' AND rating IS NOT NULL AND cover_raw_url IS NOT NULL";

    private static readonly string[] GateColumns = ["state", "type", "rating", "cover_raw_url"];

    private static (string Name, string[] Columns, string Sql)[] IndexDefinitions() =>
    [
        // Popular, and the shared prefix for anything ordering by global popularity.
        ("ix_browse_pop", ["popularity_global_current"], $"""
            CREATE INDEX ix_browse_pop ON series (popularity_global_current)
            WHERE {BrowseGate} AND popularity_global_current IS NOT NULL
            """),

        // Trending sorts on (history - current), which no index can order directly; carrying both
        // columns still lets the planner walk the far smaller filtered set instead of the table.
        ("ix_browse_trend", ["popularity_global_current", "popularity_global_history_1mo"], $"""
            CREATE INDEX ix_browse_trend ON series (popularity_global_current, popularity_global_history_1mo)
            WHERE {BrowseGate} AND popularity_global_current IS NOT NULL
              AND popularity_global_history_1mo IS NOT NULL
            """),

        ("ix_browse_new", ["published_start_date"], $"""
            CREATE INDEX ix_browse_new ON series (published_start_date DESC)
            WHERE {BrowseGate} AND published_start_date IS NOT NULL
            """),

        ("ix_browse_rating", ["rating", "popularity_global_current"], $"""
            CREATE INDEX ix_browse_rating ON series (rating DESC)
            WHERE {BrowseGate} AND popularity_global_current IS NOT NULL
            """),

        // Serves both PopularManhwa and PopularManhua.
        ("ix_browse_type", ["type", "popularity_type_current"], $"""
            CREATE INDEX ix_browse_type ON series (type, popularity_type_current)
            WHERE {BrowseGate} AND popularity_type_current IS NOT NULL
            """),

        // Not a browse index: this one serves SemanticRecommender's duplicate-seed exclusion, which
        // looks a seed's title back up to find the dump's second entry for the same work. Without it
        // that lookup is a full scan of the same ~558k rows - 1.5s on an idle machine, against 0.9ms
        // with it - and it is paid once per uncached recommendation request, which for the
        // single-seed "More like this" rail is a ~70ms request. Costs 1.8s to build. Restricted to
        // active rows because the exclusion only ever asks about those, which keeps it small.
        ("ix_title_nocase", ["title", "state"], """
            CREATE INDEX ix_title_nocase ON series (title COLLATE NOCASE)
            WHERE state = 'active'
            """),
    ];

    /// <summary>
    /// Indexes the columns the Discover rails filter and sort on. The dump ships with <b>no indexes
    /// at all</b>, so without these every rail is a full scan of ~558k rows across ~3.5 GB plus a
    /// sort: measured at 11s for the six-rail set, and far worse whenever the page cache is cold or
    /// the disk is busy, which is what makes the endpoint's tail latency unbounded.
    ///
    /// <para>
    /// Measured 17.25s to 0.43s over the six rails, a 40x improvement, with every rail reporting an
    /// index rather than a scan. Costs ~15s to build and ~13 MB.
    /// </para>
    ///
    /// <para>
    /// A missing column skips that one index and logs, rather than throwing: these are an
    /// optimization, and failing here would fail the whole dump refresh and stop metadata updating.
    /// </para>
    /// </summary>
    internal static void BuildBrowseIndexes(SqliteConnection conn, ILogger? logger = null)
    {
        var present = ColumnsOf(conn);

        foreach (var name in BrowseIndexNames)
        {
            Execute(conn, $"DROP INDEX IF EXISTS {name}");
        }

        var built = 0;
        foreach (var (name, columns, sql) in IndexDefinitions())
        {
            var missing = GateColumns.Concat(columns).Where(c => !present.Contains(c)).ToList();
            if (missing.Count > 0)
            {
                logger?.LogWarning(
                    "Skipping MangaBaka browse index {Index}: the dump has no {Columns} column(s). " +
                    "The matching Discover rail will fall back to a full scan.",
                    name, string.Join(", ", missing));
                continue;
            }

            Execute(conn, sql);
            built++;
        }

        // Without stats the planner has been observed preferring a scan over a partial index on a
        // table this size. Cheap here because the indexes are already built.
        if (built > 0)
        {
            Execute(conn, "ANALYZE");
        }
    }


    /// <summary>
    /// Builds the browse indexes on the <em>installed</em> dump when they are missing, and does
    /// nothing when they are already there.
    ///
    /// <para>
    /// Needed because the indexes are otherwise only created on the staged file during a download,
    /// and the dump only downloads when its published SHA1 changes. Without this an install that
    /// already has a current dump would keep paying the full-scan cost until MangaBaka happened to
    /// publish a new one - which is every existing install on upgrade, the exact case that reported
    /// the slow endpoint.
    /// </para>
    /// </summary>
    public async Task EnsureBrowseIndexesAsync(CancellationToken ct = default)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return;
        }

        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False");
        await conn.OpenAsync(ct);

        // Compared against what this dump can actually support, not against the full list: a dump
        // missing a column can never reach the full count, and testing for it would rebuild every
        // index on every job tick forever.
        var expected = BuildableIndexNames(conn);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_browse_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        if (expected.SetEquals(existing))
        {
            return;
        }

        logger.LogInformation(
            "Backfilling MangaBaka browse indexes ({Existing} of {Expected} present)…",
            existing.Count, expected.Count);
        var started = DateTime.UtcNow;
        BuildBrowseIndexes(conn, logger);
        logger.LogInformation(
            "MangaBaka browse indexes built in {Elapsed:F1}s", (DateTime.UtcNow - started).TotalSeconds);
    }

    /// <summary>Indexes every title variant of non-merged series into the FTS5 search table.</summary>
    internal static void BuildSearchIndex(SqliteConnection conn)
    {
        Execute(conn, $"DROP TABLE IF EXISTS {SearchTableName}");
        Execute(conn, $"CREATE VIRTUAL TABLE {SearchTableName} USING fts5(title, series_id UNINDEXED, tokenize='unicode61 remove_diacritics 2')");

        using var tx = conn.BeginTransaction();
        foreach (var column in new[] { "title", "native_title", "romanized_title" })
        {
            Execute(conn, $"""
                INSERT INTO {SearchTableName} (series_id, title)
                SELECT id, [{column}] FROM series
                WHERE state = 'active' AND [{column}] IS NOT NULL
                """, tx);
        }

        // The titles column holds every alternative title as JSON: [{"title": …, "language": …}, …]
        Execute(conn, $"""
            INSERT INTO {SearchTableName} (series_id, title)
            SELECT s.id, json_extract(je.value, '$.title')
            FROM series s, json_each(s.titles) je
            WHERE s.state = 'active' AND s.titles IS NOT NULL
              AND json_extract(je.value, '$.title') IS NOT NULL
            """, tx);
        tx.Commit();
    }

    private async Task SwapIntoPlaceAsync(string stagingPath, CancellationToken ct)
    {
        // Readers use Pooling=False, but an in-flight query may still hold the old file
        // open for a moment — retry the move instead of failing the whole refresh.
        SqliteConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(stagingPath, options.DatabasePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.CommandTimeout = 600; // FTS population scans the full 3 GB table
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Pass-through read stream that feeds every byte it serves into an IncrementalHash.</summary>
internal sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            hash.AppendData(buffer, offset, read);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        if (read > 0)
        {
            hash.AppendData(buffer.Span[..read]);
        }

        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
