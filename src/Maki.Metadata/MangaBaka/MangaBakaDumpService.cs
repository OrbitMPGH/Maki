using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Maki.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;
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
    MangaBakaDumpStatus status,
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

    /// <summary>Live progress of the refresh in flight, for the settings UI and the progress toast.</summary>
    public MangaBakaDumpProgress Progress() => status.Snapshot();

    /// <summary>
    /// True when the installed dump failed its integrity check and has not been replaced yet. Read
    /// from the last refresh, never checked here, so it costs nothing to ask.
    /// </summary>
    public bool Damaged => status.Damage is not null;

    /// <summary>Downloads and installs the dump if its checksum changed; returns true when a new dump was installed.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        status.Begin();
        try
        {
            var installed = await RefreshCoreAsync(ct);
            status.End(installed, null);
            return installed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure: the job treats it as one and so should the UI.
            status.End(false, null);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout surfaces as a cancellation nobody asked for. Left as one, the UI
            // would show no error and the gate would keep saying the download is running.
            var timeout = new TimeoutException("The MangaBaka dump download timed out", ex);
            status.End(false, timeout.Message);
            throw timeout;
        }
        catch (Exception ex)
        {
            status.End(false, ex.Message);
            throw;
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken ct)
    {
        // A run killed mid-build (a SIGKILL once Docker's stop grace runs out) leaves up to 4 GB behind,
        // and an unchanged dump never downloads again to overwrite it.
        var stagingPath = Path.Combine(options.StagingDirectory, "mangabaka.db.partial");
        DeleteStaging(stagingPath);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var dumpPath = await DumpPathAsync(ct);

        // Published as "<hex sha1>  <filename>" over the compressed file.
        var sha1Line = await client.GetStringAsync(dumpPath + ".sha1", ct);
        var expectedSha1 = sha1Line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(expectedSha1))
        {
            throw new InvalidOperationException($"MangaBaka dump checksum response from {dumpPath}.sha1 was empty");
        }

        var installedSha1 = await settings.GetAsync(SettingKeys.MangaBakaDumpSha1, ct);
        if (string.Equals(expectedSha1, installedSha1, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(options.DatabasePath))
        {
            var problem = IntegrityProblem(options.DatabasePath);
            status.Damage = problem;
            if (problem is null)
            {
                logger.LogDebug("MangaBaka dump unchanged ({Sha1}); skipping download", expectedSha1);
                return false;
            }

            logger.LogWarning("The installed MangaBaka dump looks damaged ({Problem}); downloading it again", problem);
        }

        Directory.CreateDirectory(options.StagingDirectory);

        try
        {
            var actualSha1 = await DownloadAndDecompressAsync(client, dumpPath, stagingPath, ct);
            if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"MangaBaka dump checksum mismatch: expected {expectedSha1}, got {actualSha1}");
            }

            status.SetPhase("indexing");
            PrepareStagedDatabase(stagingPath, ct);
            status.SetPhase("installing");
            await SwapIntoPlaceAsync(stagingPath, ct);
        }
        catch
        {
            DeleteStaging(stagingPath);
            throw;
        }

        status.Damage = null;
        await settings.SetAsync(SettingKeys.MangaBakaDumpSha1, expectedSha1, ct);
        await settings.SetAsync(SettingKeys.MangaBakaDumpRefreshedAt, DateTime.UtcNow.ToString("O"), ct);
        logger.LogInformation("MangaBaka local database installed at {Path} ({Sha1})", options.DatabasePath, expectedSha1);
        return true;
    }

    /// <summary>
    /// Why the installed dump cannot be used, or null when it looks whole. Compares the header's own
    /// page count with the file length (a truncated copy), then opens it and reads the first and last
    /// series rows and checks the search table is there. That costs milliseconds on the 3.5 GB file,
    /// where <c>PRAGMA quick_check</c> would read all of it, so it only runs when the refresh is about
    /// to skip the download, never on a metadata lookup.
    /// </summary>
    internal static string? IntegrityProblem(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            var header = new byte[100];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                {
                    return "shorter than a SQLite header";
                }
            }

            if (!header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            {
                return "not a SQLite database";
            }

            // The in-header page count is only trustworthy while the version-valid-for number
            // matches the change counter; SQLite itself falls back to the file size otherwise.
            long pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
            if (pageSize == 1)
            {
                pageSize = 65536;
            }

            var changeCounter = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));
            var pages = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
            var validFor = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(92));
            if (pages > 0 && validFor == changeCounter && length < pages * pageSize)
            {
                return $"truncated: {length} bytes of {pages * pageSize}";
            }

            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var tables = (long)Scalar(conn,
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('series', '{SearchTableName}')")!;
            if (tables < 2)
            {
                return "the series or search table is missing";
            }

            if (Scalar(conn, "SELECT (SELECT id FROM series LIMIT 1) + (SELECT MAX(rowid) FROM series)") is null or DBNull)
            {
                return "the series table is empty";
            }

            return null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Streams the zst dump to disk, decompressing on the fly while hashing the compressed bytes.</summary>
    private async Task<string> DownloadAndDecompressAsync(
        HttpClient client, string dumpPath, string stagingPath, CancellationToken ct)
    {
        logger.LogInformation("Downloading MangaBaka database dump ({Dump})…", dumpPath);
        using var response = await client.GetAsync(dumpPath, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Content-Length is the compressed size, which is what the hashing stream counts, so the
        // two are comparable. A server that withholds it leaves the UI with bytes but no percentage.
        status.BeginDownload(response.Content.Headers.ContentLength);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var source = new StallTimeoutStream(await response.Content.ReadAsStreamAsync(ct));
        await using var hashing = new HashingReadStream(source, sha1, status.ReportDownloaded);
        await using var decompressed = new DecompressionStream(hashing);
        await using (var output = File.Create(stagingPath))
        {
            await decompressed.CopyToAsync(output, ct);
        }

        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    /// <summary>Sanity-checks the staged database and builds the FTS5 index over all title variants.</summary>
    private void PrepareStagedDatabase(string stagingPath, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={stagingPath};Pooling=False");
        conn.Open();

        Interruptible(conn, ct, () =>
        {
            var count = (long)Scalar(conn, "SELECT COUNT(*) FROM series")!;
            if (count < 1000)
            {
                throw new InvalidOperationException($"MangaBaka dump looks broken: only {count} series rows");
            }

            logger.LogInformation("Building MangaBaka search index over {Count} series…", count);
            BuildSearchIndex(conn);

            logger.LogInformation("Building MangaBaka browse indexes…");
            BuildBrowseIndexes(conn, logger);
        });
    }

    /// <summary>
    /// Runs <paramref name="work"/> so that <paramref name="ct"/> stops the statement in flight.
    /// Microsoft.Data.Sqlite never looks at a token once a statement is running, and the builds here
    /// are single statements over the whole 3.5 GB file that take minutes, so a shutdown would wait
    /// out all of it. The progress handler aborts the statement with SQLITE_INTERRUPT within a few
    /// thousand VM steps of cancellation, which is rethrown as the cancellation it is.
    /// </summary>
    internal static void Interruptible(SqliteConnection conn, CancellationToken ct, Action work)
    {
        ct.ThrowIfCancellationRequested();
        raw.sqlite3_progress_handler(conn.Handle, 10_000, _ => ct.IsCancellationRequested ? 1 : 0, null);
        try
        {
            work();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The MangaBaka index build was cancelled", ex, ct);
        }
        finally
        {
            raw.sqlite3_progress_handler(conn.Handle, 0, null, null);
        }
    }

    /// <summary>The names of the indexes <see cref="BuildBrowseIndexes"/> creates.</summary>
    private static readonly string[] BrowseIndexNames =
    [
        "ix_browse_pop", "ix_browse_trend", "ix_browse_new", "ix_browse_rating", "ix_browse_type",
        "ix_title_nocase", "ix_ext_anilist", "ix_ext_mal", "ix_ext_kitsu", "ix_ext_mangaupdates",
    ];

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
            CREATE INDEX IF NOT EXISTS ix_browse_pop ON series (popularity_global_current)
            WHERE {BrowseGate} AND popularity_global_current IS NOT NULL
            """),

        // Trending sorts on (history - current), which no index can order directly; carrying both
        // columns still lets the planner walk the far smaller filtered set instead of the table.
        ("ix_browse_trend", ["popularity_global_current", "popularity_global_history_1mo"], $"""
            CREATE INDEX IF NOT EXISTS ix_browse_trend ON series (popularity_global_current, popularity_global_history_1mo)
            WHERE {BrowseGate} AND popularity_global_current IS NOT NULL
              AND popularity_global_history_1mo IS NOT NULL
            """),

        ("ix_browse_new", ["published_start_date"], $"""
            CREATE INDEX IF NOT EXISTS ix_browse_new ON series (published_start_date DESC)
            WHERE {BrowseGate} AND published_start_date IS NOT NULL
            """),

        ("ix_browse_rating", ["rating", "popularity_global_current"], $"""
            CREATE INDEX IF NOT EXISTS ix_browse_rating ON series (rating DESC)
            WHERE {BrowseGate} AND popularity_global_current IS NOT NULL
            """),

        // Serves both PopularManhwa and PopularManhua.
        ("ix_browse_type", ["type", "popularity_type_current"], $"""
            CREATE INDEX IF NOT EXISTS ix_browse_type ON series (type, popularity_type_current)
            WHERE {BrowseGate} AND popularity_type_current IS NOT NULL
            """),

        // Not a browse index: this one serves SemanticRecommender's duplicate-seed exclusion, which
        // looks a seed's title back up to find the dump's second entry for the same work. Without it
        // that lookup is a full scan of the same ~558k rows - 1.5s on an idle machine, against 0.9ms
        // with it - and it is paid once per uncached recommendation request, which for the
        // single-seed "More like this" rail is a ~70ms request. Costs 1.8s to build. Restricted to
        // active rows because the exclusion only ever asks about those, which keeps it small.
        ("ix_title_nocase", ["title", "state"], """
            CREATE INDEX IF NOT EXISTS ix_title_nocase ON series (title COLLATE NOCASE)
            WHERE state = 'active'
            """),

        // Serve MangaBakaLocalStore.GetIdsByExternalIdsAsync (anime signals, import lists), whose
        // chunked IN (...) lookups were a full scan per 500 ids without them: 1.1 s warm and 5.5 s
        // cold per chunk. Partial because most rows carry none of these ids, which keeps each small.
        ("ix_ext_anilist", ["source_anilist_id"], """
            CREATE INDEX IF NOT EXISTS ix_ext_anilist ON series (source_anilist_id)
            WHERE source_anilist_id IS NOT NULL
            """),

        ("ix_ext_mal", ["source_my_anime_list_id"], """
            CREATE INDEX IF NOT EXISTS ix_ext_mal ON series (source_my_anime_list_id)
            WHERE source_my_anime_list_id IS NOT NULL
            """),

        ("ix_ext_kitsu", ["source_kitsu_id"], """
            CREATE INDEX IF NOT EXISTS ix_ext_kitsu ON series (source_kitsu_id)
            WHERE source_kitsu_id IS NOT NULL
            """),

        // MangaUpdates ids are text slugs, looked up lowercased (GetIdsByMangaUpdatesIdsAsync), so
        // this indexes the lowercased value; a plain column index would not serve that query.
        ("ix_ext_mangaupdates", ["source_manga_updates_id"], """
            CREATE INDEX IF NOT EXISTS ix_ext_mangaupdates ON series (lower(source_manga_updates_id))
            WHERE source_manga_updates_id IS NOT NULL
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
    /// All are <b>partial</b> indexes over the rails' common quality gate (see <c>BrowseGate</c>), which
    /// must stay in step with <c>MangaBakaLocalStore.GetBrowseAsync</c>.
    /// </para>
    ///
    /// <para>
    /// A missing column skips that one index and logs, rather than throwing: these are an
    /// optimization, and failing here would fail the whole dump refresh and stop metadata updating.
    /// </para>
    ///
    /// <para>
    /// <paramref name="onlyMissing"/> is for the installed file, which readers are querying while
    /// this runs: it drops nothing and creates only what is absent, so the indexes already there
    /// keep serving the rails. The staged file drops and rebuilds the whole set.
    /// </para>
    /// </summary>
    internal static void BuildBrowseIndexes(SqliteConnection conn, ILogger? logger = null, bool onlyMissing = false)
    {
        var present = ColumnsOf(conn);
        var existing = onlyMissing ? IndexesOf(conn) : [];

        if (!onlyMissing)
        {
            foreach (var name in BrowseIndexNames)
            {
                Execute(conn, $"DROP INDEX IF EXISTS {name}");
            }
        }

        var builtNames = new List<string>();
        foreach (var (name, columns, sql) in IndexDefinitions())
        {
            if (existing.Contains(name))
            {
                continue;
            }

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
            builtNames.Add(name);
        }

        // Without stats the planner has been observed preferring a scan over a partial index on a
        // table this size. Cheap here because the indexes are already built. On the live file only
        // the new indexes are analyzed, so the ones already serving queries are left alone.
        if (onlyMissing)
        {
            foreach (var name in builtNames)
            {
                Execute(conn, $"ANALYZE {name}");
            }
        }
        else if (builtNames.Count > 0)
        {
            Execute(conn, "ANALYZE");
        }
    }

    private static HashSet<string> IndexesOf(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            existing.Add(reader.GetString(0));
        }

        return existing;
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
    public async Task<bool> EnsureBrowseIndexesAsync(CancellationToken ct = default)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return false;
        }

        await using var conn = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False");
        await conn.OpenAsync(ct);

        // Compared against what this dump can actually support, not against the full list: a dump
        // missing a column can never reach the full count, and testing for it would rebuild every
        // index on every job tick forever.
        var expected = BuildableIndexNames(conn);
        var existing = IndexesOf(conn);

        // Any write changes the dump stamp and invalidates the in-memory catalogue indexes, so a
        // complete set must leave the file untouched.
        if (expected.IsSubsetOf(existing))
        {
            return false;
        }

        logger.LogInformation(
            "Backfilling MangaBaka browse indexes ({Existing} of {Expected} present)…",
            expected.Count(existing.Contains), expected.Count);
        var started = DateTime.UtcNow;
        Interruptible(conn, ct, () => BuildBrowseIndexes(conn, logger, onlyMissing: true));
        logger.LogInformation(
            "MangaBaka browse indexes built in {Elapsed:F1}s", (DateTime.UtcNow - started).TotalSeconds);
        return true;
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
        // Readers use Pooling=False, but an in-flight query or an index build can hold the old
        // file open for several seconds, so retry the move instead of failing the whole refresh.
        SqliteConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A journal or WAL left by an interrupted write belongs to the file being replaced;
                // SQLite would replay it onto the new one on first open.
                foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                {
                    File.Delete(options.DatabasePath + suffix);
                }

                File.Move(stagingPath, options.DatabasePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 15)
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

    /// <summary>Removes the staged file and the rollback journal an interrupted build leaves beside it.</summary>
    private static void DeleteStaging(string stagingPath)
    {
        foreach (var path in new[] { stagingPath, stagingPath + "-journal" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// Pass-through read stream that fails a read which delivers nothing for <paramref name="stall"/>.
/// With <see cref="HttpCompletionOption.ResponseHeadersRead"/> the client timeout stops at the
/// headers, so a connection that goes quiet mid-body would otherwise block the copy forever, and
/// with it <c>ArtifactBuildGate</c> and every job queued behind it.
/// </summary>
internal sealed class StallTimeoutStream(Stream inner, TimeSpan? stall = null, TimeProvider? time = null) : Stream
{
    public static readonly TimeSpan DefaultStall = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _stall = stall ?? DefaultStall;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    private CancellationTokenSource? _timer;
    private CancellationTokenSource? _quiet;
    private CancellationToken _linkedTo;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // One timer per stream, armed for each read and disarmed after, rather than a new one per
        // read. Rebuilt only for a different token, or once it has fired.
        if (_timer is null || _quiet is null || ct != _linkedTo || _quiet.IsCancellationRequested)
        {
            _quiet?.Dispose();
            _timer?.Dispose();
            _timer = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time ?? TimeProvider.System);
            _quiet = CancellationTokenSource.CreateLinkedTokenSource(ct, _timer.Token);
            _linkedTo = ct;
        }

        _timer.CancelAfter(_stall);
        try
        {
            var read = await inner.ReadAsync(buffer, _quiet.Token);
            _timer.CancelAfter(Timeout.InfiniteTimeSpan);
            return read;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Download stalled: no data for {_stall.TotalSeconds:F0} s");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _quiet?.Dispose();
            _timer?.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Pass-through read stream that feeds every byte it serves into an IncrementalHash, and reports
/// the running total to <paramref name="onProgress"/> so the transfer is observable. The callback
/// is on the read path, so it must stay cheap: <see cref="MangaBakaDumpStatus"/> throttles the rate
/// maths itself rather than making this stream decide when to call.
/// </summary>
internal sealed class HashingReadStream(
    Stream inner, IncrementalHash hash, Action<long>? onProgress = null) : Stream
{
    private long _read;


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
            _read += read;
            onProgress?.Invoke(_read);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        if (read > 0)
        {
            hash.AppendData(buffer.Span[..read]);
            _read += read;
            onProgress?.Invoke(_read);
        }

        return read;
    }

    // ZstdSharp's DecompressionStream reads through this overload. Stream's default runs the
    // synchronous Read on the pool, which drops the token and skips StallTimeoutStream's timer.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
