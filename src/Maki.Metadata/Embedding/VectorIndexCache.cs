using System.Buffers;
using Maki.Core.Io;
using Maki.Core;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Taste;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Owns the process-wide <see cref="VectorIndex"/>: builds it on first use from the vector DB
/// joined to the dump, then hands the same instance to every search. Vectors are stored int8
/// already, so the build copies payloads verbatim and only reads the dump columns the filters need. The build is a full scan of
/// both DBs (seconds), so it happens once. It is dropped when the embedding index is rebuilt (call
/// <see cref="Invalidate"/> after an indexing pass) and when the dump file changes, which the next
/// read notices by itself.
///
/// The index is immutable once built, so readers need no lock; only the build is serialized.
/// </summary>
public sealed class VectorIndexCache(
    EmbeddingOptions options,
    MangaBakaDumpOptions dumpOptions,
    ILogger<VectorIndexCache> logger,
    TasteVectorOptions? tasteOptions = null)
{
    /// <summary>
    /// Candidate predicate — must stay in sync with <see cref="SeriesEmbeddingIndexer"/>'s, since
    /// those are the rows that actually have vectors. No content-rating floor here either: rows of
    /// every rating load into the index, and <see cref="VectorIndex.Matches"/> is what bounds a
    /// given search to whatever ceiling the caller resolved into its <see cref="FilterPlan"/>.
    /// </summary>
    private const string CandidateWhere =
        "d.state = 'active' AND d.rating IS NOT NULL AND d.type != 'novel'";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// The index with the dump's write time and length at build time. The index snapshots rating,
    /// popularity, genres and content rating from the dump, so a nightly swap has to rebuild it,
    /// the same way <see cref="Catalogue.CatalogueIndexCache"/> is stamped.
    /// </summary>
    private sealed record Loaded(VectorIndex Index, long DumpTicks, long DumpLength, long Generation);

    private volatile Loaded? _loaded;

    /// <summary>
    /// A build that found nothing usable, with what it read. Without it every search and every
    /// recommendation request after a model change re-runs a full scan that is still empty: the
    /// stored vectors all have the old width until the prebuilt index or a local pass replaces them.
    /// </summary>
    private sealed record EmptyBuild(long DumpTicks, long DumpLength, long Generation, string Vectors);

    private volatile EmptyBuild? _empty;
    private readonly IdleStamp _idle = new();

    // Bumped by every invalidation, so a build that was already reading when one landed cannot
    // publish what it read.
    private readonly object _publish = new();
    private long _generation;

    /// <summary>Test hook: the latest background snapshot write.</summary>
    internal Task SnapshotSavedForTest { get; private set; } = Task.CompletedTask;

    /// <summary>Test hook: runs after a build finishes reading and before it is published.</summary>
    internal Action? AfterBuildForTest { get; set; }
    private readonly SharedBuild<Loaded> _builds = new();
    private int _warming;

    /// <summary>Whether the search vectors are in memory, for the memory diagnostics.</summary>
    public bool IsLoaded => _loaded is not null;

    /// <summary>
    /// Identifies the loaded index: it changes when the dump it was built from is replaced or the
    /// index is invalidated. Null while nothing is loaded.
    /// </summary>
    public string? Stamp => _loaded is { } loaded ? $"{loaded.DumpTicks}:{loaded.DumpLength}:{loaded.Generation}" : null;

    /// <summary>Whether an index is loaded and was built from the dump on disk now.</summary>
    public bool IsCurrent => _loaded is { } loaded && MatchesDump(loaded);

    /// <summary>
    /// The loaded index when it is current, without building one or counting as a read. For a
    /// caller that only needs to know which index is live.
    /// </summary>
    public VectorIndex? TryGetLoaded() => _loaded is { } loaded && MatchesDump(loaded) ? loaded.Index : null;

    /// <summary>
    /// Starts building the index on the thread pool unless it is current or a build is already
    /// running. For a request that would rather answer without the index this time than wait
    /// seconds for it.
    /// </summary>
    public void WarmInBackground()
    {
        if (IsCurrent || _builds.IsRunning || KnownEmpty() || Interlocked.CompareExchange(ref _warming, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await GetAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background build of the search vectors failed");
            }
            finally
            {
                Volatile.Write(ref _warming, 0);
            }
        });
    }

    private bool KnownEmpty() =>
        _empty is { } empty && DumpInfo() is { } info
        && info.LastWriteTimeUtc.Ticks == empty.DumpTicks && info.Length == empty.DumpLength
        && empty.Generation == Interlocked.Read(ref _generation) && empty.Vectors == VectorFileStamp();

    /// <summary>
    /// The vector database's content version, the same one the snapshot key carries. File times
    /// cannot stand in: a checkpoint or a reader moves them without changing anything stored.
    /// </summary>
    private string VectorFileStamp()
    {
        try
        {
            SqliteKeeper.For(options.VectorDbPath).Attach();
            using var conn = new SqliteConnection($"Data Source={options.VectorDbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            return ContentStamp(conn);
        }
        catch (SqliteException)
        {
            return "-";
        }
    }

    private bool MatchesDump(Loaded loaded) =>
        DumpInfo() is { } info && info.LastWriteTimeUtc.Ticks == loaded.DumpTicks && info.Length == loaded.DumpLength;

    private FileInfo? DumpInfo()
    {
        if (!File.Exists(dumpOptions.DatabasePath))
        {
            return null;
        }

        var info = new FileInfo(dumpOptions.DatabasePath);
        return info.Exists ? info : null;
    }

    /// <summary>How long since anything read them. Meaningless while unloaded.</summary>
    public TimeSpan IdleFor => _idle.Idle;

    /// <summary>Whether anything read them within <paramref name="window"/>, loaded now or not.</summary>
    public bool UsedWithin(TimeSpan window) => _idle.UsedWithin(window);

    /// <summary>
    /// Drops the built index when nothing has read it for <paramref name="idleFor"/>, and reports
    /// whether it did.
    ///
    /// <para>
    /// This used to be excluded from the idle unload on the grounds that every recommendation and
    /// every search needs it, so it is the likeliest artifact to be wanted again straight after
    /// being dropped. Measurement on a real instance is what changed that: at 16 minutes the
    /// process held 104 MB of large-object heap and the vectors were the largest single part of it,
    /// on an instance whose owner was not searching. A NAS with 8 GB cannot carry that for a
    /// feature nobody is using.
    /// </para>
    ///
    /// <para>
    /// The original reasoning survives in the window rather than in an exemption: this one is
    /// deliberately longer than the graphs' (<c>MAKI_VECTOR_IDLE_MINUTES</c>, default 15),
    /// because the rebuild is about eight seconds of reading every vector BLOB and a person who
    /// searches once tends to search again. No lock, for the same reason as the other caches: the
    /// index is immutable and a reader holds its own reference.
    /// </para>
    /// </summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        var idle = _idle.Idle;
        if (_loaded is null || idle < idleFor)
        {
            return false;
        }

        _loaded = null;
        logger.LogInformation(
            "Unloaded the search vectors after {Minutes:F0} idle minute(s); they rebuild on next use",
            idle.TotalMinutes);
        return true;
    }

    /// <summary>
    /// Where the built index is kept between unloads (<see cref="VectorIndexSnapshot"/>), beside
    /// the vector database it is built from.
    /// </summary>
    internal string SnapshotPath =>
        Path.Combine(Path.GetDirectoryName(options.VectorDbPath) ?? ".", Path.GetFileNameWithoutExtension(options.VectorDbPath) + ".vecindex");

    private void DeleteSnapshot()
    {
        try
        {
            File.Delete(SnapshotPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete the search vector snapshot");
        }
    }

    /// <summary>
    /// Serves <paramref name="index"/> in place of the loaded one, for an eval tool grading a
    /// modified copy (<see cref="VectorIndex.Requantized"/>) through the services that read this cache.
    /// </summary>
    internal void ReplaceLoadedForEval(VectorIndex index)
    {
        lock (_publish)
        {
            if (_loaded is { } loaded)
            {
                _loaded = loaded with { Index = index };
            }
        }
    }

    /// <summary>Drops the cached index so the next search rebuilds it. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        lock (_publish)
        {
            _generation++;
            _loaded = null;
            DeleteSnapshot();
        }

        logger.LogDebug("Search vector index invalidated");
    }

    /// <summary>
    /// Replaces the vector database with <paramref name="stagedPath"/> and drops the cached index.
    /// Waits out any running build under the build lock, so a swap can never race a build that is
    /// midway through reading the old file.
    /// <para>
    /// An existing database gets the new content copied into it with SQLite's backup API rather than
    /// a file moved over it, so the file and its <c>-shm</c> stay the ones every connection and the
    /// keeper share (see <see cref="SqliteKeeper"/>). Unlike the instant move it replaced, the copy
    /// holds the build lock and the database's write lock for its whole length, seconds on a NAS.
    /// A move is the fallback only when the copy cannot work (a page size the live file cannot take,
    /// or a corrupt live file), and then the WAL sidecars go with the old file: leaving them would
    /// let SQLite reconstruct pages of the *previous* database over the new one.
    /// </para>
    /// </summary>
    /// <returns>
    /// False when a writer held the database through the wait. Nothing changed and the staged file
    /// is left where it was; the caller tries again on its next run.
    /// </returns>
    public async Task<bool> SwapDatabaseAsync(string stagedPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await _builds.DrainAsync();
            lock (_publish)
            {
                _generation++;
                _loaded = null;
                DeleteSnapshot();
            }

            SqliteConnection.ClearAllPools();
            var copy = File.Exists(options.VectorDbPath) ? CopyInto(stagedPath) : CopyResult.Unusable;
            if (copy == CopyResult.Busy)
            {
                logger.LogInformation("Did not swap in the new vector database: {Path} is being written to", options.VectorDbPath);
                return false;
            }

            if (copy == CopyResult.Unusable)
            {
                using var keeperClosed = SqliteKeeper.For(options.VectorDbPath).Suspend();
                foreach (var sidecar in new[] { options.VectorDbPath + "-wal", options.VectorDbPath + "-shm" })
                {
                    if (File.Exists(sidecar))
                    {
                        File.Delete(sidecar);
                    }
                }

                File.Move(stagedPath, options.VectorDbPath, overwrite: true);
            }

            logger.LogInformation("Swapped in a new vector database at {Path}", options.VectorDbPath);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private enum CopyResult { Copied, Busy, Unusable }

    private CopyResult CopyInto(string stagedPath)
    {
        try
        {
            SqliteKeeper.For(options.VectorDbPath).Attach();
            using var source = new SqliteConnection($"Data Source={stagedPath};Mode=ReadOnly;Pooling=False");
            using var live = new SqliteConnection($"Data Source={options.VectorDbPath};Mode=ReadWrite;Pooling=False");
            source.Open();
            live.Open();
            using (var setup = live.CreateCommand())
            {
                setup.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_size_limit=8388608;";
                setup.ExecuteNonQuery();
            }

            source.BackupDatabase(live);

            // The copy went through the WAL whole; give the space back.
            using var truncate = live.CreateCommand();
            truncate.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var result = truncate.ExecuteReader();
            if (result.Read() && result.GetInt32(0) != 0)
            {
                logger.LogInformation("The new vector database is in place, but a reader kept its WAL from being truncated");
            }

            return CopyResult.Copied;
        }
        catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) is 5 or 6)
        {
            return CopyResult.Busy;
        }
        catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) is 8 or 11 or 26)
        {
            logger.LogWarning("Could not copy the new vector database into {Path}, replacing the file instead: {Error}",
                options.VectorDbPath, ex.Message);
            return CopyResult.Unusable;
        }
    }

    /// <summary>
    /// The index, building it if needed. Null when there's nothing to search — no vector DB, no
    /// dump, or an index that hasn't been built yet. Embeddings being switched off does not stop it:
    /// the vectors stay on disk, and the never-show list, tag filters and franchise lookups all read
    /// the index whether or not the query model is loaded.
    /// </summary>
    public async Task<VectorIndex?> GetAsync(CancellationToken ct = default)
    {
        if (_loaded is { } cached && MatchesDump(cached))
        {
            _idle.Touch();
            return cached.Index;
        }

        while (true)
        {
            Task<Loaded?> build;
            bool started;
            await _lock.WaitAsync(ct);
            try
            {
                if (_loaded is { } raced && MatchesDump(raced))
                {
                    _idle.Touch();
                    return raced.Index;
                }

                if (!File.Exists(options.VectorDbPath) || DumpInfo() is null || KnownEmpty())
                {
                    return null;
                }

                if (_loaded is not null && !_builds.IsRunning)
                {
                    logger.LogInformation("Rebuilding the search vectors because the dump file changed");
                    _loaded = null;
                }

                build = _builds.Join(BuildAndPublish, out started);
            }
            finally
            {
                _lock.Release();
            }

            var loaded = await build.WaitAsync(ct);
            // A build this caller joined part way through may have read an older dump than the one
            // on disk now, or started before an invalidation; go round again rather than hand that out.
            if (loaded is null || started || DumpInfo() is null
                || MatchesDump(loaded) && loaded.Generation == Interlocked.Read(ref _generation))
            {
                return loaded?.Index;
            }
        }
    }

    private Loaded? BuildAndPublish()
    {
        if (DumpInfo() is not { } dump)
        {
            return null;
        }

        // Stamped before the build, so a dump swapped in while it runs reads as stale next time.
        var ticks = dump.LastWriteTimeUtc.Ticks;
        var length = dump.Length;
        long generation;
        lock (_publish)
        {
            generation = _generation;
        }

        // Taken before the build, so a write landing while it reads clears the memo.
        var vectors = VectorFileStamp();
        try
        {
            var (built, fresh, key) = Build(ticks, length, CancellationToken.None);
            AfterBuildForTest?.Invoke();
            var loaded = built is null ? null : new Loaded(built, ticks, length, generation);
            var current = false;
            lock (_publish)
            {
                if (generation == _generation)
                {
                    _loaded = loaded;
                    _empty = loaded is null ? new EmptyBuild(ticks, length, generation, vectors) : null;
                    current = true;
                }
            }

            _idle.Touch();
            if (current && fresh is not null && built is not null)
            {
                SaveSnapshot(built, fresh, key, generation);
            }

            return loaded;
        }
        catch (Exception ex)
        {
            // Logged here because every caller may have stopped waiting by now.
            logger.LogWarning(ex, "Building the search vectors failed");
            throw;
        }
        finally
        {
            // The build reads every vector BLOB in the file end to end; none of those pages is
            // wanted again until the next rebuild.
            PageCache.DropAfterScan(options.VectorDbPath);
        }
    }

    private static string? StoredModelVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM main.meta WHERE key = 'model_version'";
        try
        {
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private (VectorIndex? Index, VectorIndexParts? Fresh, string Key) Build(long dumpTicks, long dumpLength, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        SqliteKeeper.For(options.VectorDbPath).Attach();
        using var conn = new SqliteConnection($"Data Source={options.VectorDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $dump AS dump";
            attach.Parameters.AddWithValue("$dump", dumpOptions.DatabasePath);
            attach.ExecuteNonQuery();
        }

        // Size from the small vector table, then trim excluded rows after the scan. Joining the
        // dump just to count candidates can walk its multi-GB rows through a title index, costing
        // more than the entire build. The vector count is an upper bound on eligible candidates.
        int total;
        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM series_vectors";
            count.CommandTimeout = 600;
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (total == 0)
        {
            logger.LogInformation("Search vector index empty — nothing embedded yet");
            return (null, null, string.Empty);
        }

        // Served anyway: refusing would turn search off for the hours a local re-embed takes, and
        // the nightly install replaces such a file. The log is what explains poor ranking until then.
        if (StoredModelVersion(conn) is { } stored
            && !string.Equals(stored, options.ModelVersion, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "The vector database holds {Stored} vectors but this install embeds queries with {Current}; "
                + "search ranks poorly until the prebuilt index is installed or the index is rebuilt",
                stored, options.ModelVersion);
        }

        var key = SnapshotKey(conn, dumpTicks, dumpLength);
        if (TryReadSnapshot(key) is { } saved)
        {
            logger.LogInformation(
                "Loaded the search vector index from its snapshot: {Rows} series × {Dim} dims in {Elapsed:F1}s",
                saved.Ids.Length, saved.Dimensions, (DateTime.UtcNow - started).TotalSeconds);
            return (Assemble(saved, conn), null, key);
        }

        var ids = new long[total];
        var scales = new float[total];
        var years = new int[total];
        var ratings = new float[total];
        var chapters = new int[total];
        var typeIdx = new byte[total];
        var statusIdx = new byte[total];
        var genreIdx = new int[total][];
        var authorIdx = new int[total][];
        var artistIdx = new int[total][];
        var popularity = new int[total];
        // Packed as rows arrive rather than kept as a byte[] per row; see PackedTags.
        var tagBytes = new ArrayBufferWriter<byte>(Math.Max(1, total) * 40);
        var tagOffsets = new int[total + 1];
        var contentRatingIdx = new byte[total];
        var startDays = new int[total];

        // The configured model's dimensionality is authoritative, not whatever the first row
        // happens to be: after a model change the table holds both old and new vectors until the
        // re-embed finishes, and inferring the width from row one would throw away whichever
        // generation didn't come first.
        var dimensions = options.Dimensions;
        var cells = (long)total * dimensions;
        if (cells > int.MaxValue)
        {
            logger.LogWarning("Vector index too large to hold in memory ({Cells} cells)", cells);
            return (null, null, key);
        }

        // Packed as it is read, never materialized as int8: the int8 form of this table is 93 MB
        // at catalogue scale and would be a second allocation of that size alive during the build.
        var stride = EmbeddingMath.PackedStride(dimensions);
        var data = new byte[(long)total * stride];
        var mismatched = 0;

        var typeIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var statusIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var genreIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var contentRatingIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Authors are far higher-cardinality than the other vocabularies (tens of thousands of
        // names against a few hundred genres), which is a few MB of strings — worth it, because it
        // is what lets the recommender answer its author-match term without touching SQLite.
        var authorIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The browse table carries every dump column this reads, keyed and typed like series, so
        // the 127k lookups below stay inside it instead of reading the 6 KB series rows, which after
        // an idle unload are cold (the dump is dropped from the page cache after each scan).
        var dumpTable = MangaBakaDumpService.BrowseSource(conn, "dump");

        // Older dumps have no publication date, and a missing column would fail the whole build
        // rather than just the date sort, which falls back to the year.
        var startDateColumn = HasDumpColumn(conn, dumpTable, "published_start_date")
            ? "d.published_start_date"
            : "NULL";

        var rows = 0;
        using (var scan = conn.CreateCommand())
        {
            scan.CommandText = $"""
                SELECT v.id, v.scale, v.vec, d.year, d.rating, d.total_chapters, d.type, d.status,
                       d.genres, t.tags, d.authors, d.popularity_global_current, d.content_rating,
                       d.artists, {startDateColumn}
                FROM series_vectors v
                CROSS JOIN dump.{dumpTable} d ON d.id = v.id
                LEFT JOIN series_tags t ON t.id = v.id
                WHERE {CandidateWhere}
                """;
            // CROSS JOIN keeps vectors outermost: visit only embedded dump rows by primary key,
            // rather than letting SQLite scan the whole catalogue through an unrelated index.
            scan.CommandTimeout = 600;
            using var reader = scan.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (rows >= total || reader.IsDBNull(1) || reader.GetValue(2) is not byte[] blob)
                {
                    continue;
                }

                // Stored vectors are already int8 at the width this index wants, so the payload
                // copies straight in — no dequantize/requantize round trip.
                if (blob.Length != dimensions)
                {
                    mismatched++; // a row from an older model version; the next pass re-embeds it
                    continue;
                }

                ids[rows] = reader.GetInt64(0);
                // The scale carries the packing step, so a level still stands for the number the
                // stored int8 did.
                var step = EmbeddingMath.PackQuantized(
                    MemoryMarshal.Cast<byte, sbyte>(blob), data.AsSpan(rows * stride, stride));
                scales[rows] = (float)reader.GetDouble(1) * step;
                years[rows] = reader.IsDBNull(3) ? VectorIndex.Unknown : reader.GetInt32(3);
                ratings[rows] = (float)reader.GetDouble(4);
                chapters[rows] = ParseCount(GetString(reader, 5)) ?? VectorIndex.Unknown;
                typeIdx[rows] = Intern(typeIds, GetString(reader, 6));
                statusIdx[rows] = Intern(statusIds, GetString(reader, 7));
                genreIdx[rows] = ParseNames(GetString(reader, 8), genreIds);
                // Packed tag blobs ride along so the tag channel can score the whole catalogue
                // instead of only re-ranking what the dense pass already found (~20 MB).
                if (reader.GetValue(9) is byte[] tags)
                {
                    tagBytes.Write(tags);
                }

                tagOffsets[rows + 1] = tagBytes.WrittenCount;
                // One vocabulary for both roles, so a person who writes one series and draws
                // another matches across them. Sentinel names ("Anthology" is the most common value
                // in the whole column) are dropped here rather than at query time: a non-person is
                // not a credit, and filtering it once costs nothing per request.
                authorIdx[rows] = ParseNames(GetString(reader, 10), authorIds, ignoreSentinels: true);
                artistIdx[rows] = ParseNames(GetString(reader, 13), authorIds, ignoreSentinels: true);
                popularity[rows] = reader.IsDBNull(11) ? VectorIndex.Unknown : reader.GetInt32(11);
                contentRatingIdx[rows] = Intern(contentRatingIds, GetString(reader, 12));
                startDays[rows] = ParseStartDay(GetString(reader, 14)) ?? VectorIndex.Unknown;
                rows++;
            }
        }

        if (rows == 0)
        {
            logger.LogInformation(
                "Search vector index empty: no eligible catalogue rows with usable vectors " +
                "({Mismatched} stored vector(s) have the wrong model width)",
                mismatched);
            return (null, null, key);
        }

        // The vector count includes missing/ineligible dump rows and old model widths. Trim to
        // what was actually read so no zeroed rows are searchable.
        if (rows != total)
        {
            Array.Resize(ref ids, rows);
            Array.Resize(ref scales, rows);
            Array.Resize(ref years, rows);
            Array.Resize(ref ratings, rows);
            Array.Resize(ref chapters, rows);
            Array.Resize(ref typeIdx, rows);
            Array.Resize(ref statusIdx, rows);
            Array.Resize(ref genreIdx, rows);
            Array.Resize(ref authorIdx, rows);
            Array.Resize(ref artistIdx, rows);
            Array.Resize(ref popularity, rows);
            Array.Resize(ref tagOffsets, rows + 1);
            Array.Resize(ref contentRatingIdx, rows);
            Array.Resize(ref startDays, rows);

            // Not resized with the rest. Array.Resize allocates a second array and copies, and this
            // one is ~100 MB at catalogue scale: the copy doubles peak footprint during the build
            // and leaves the original as dead Large Object Heap that is never compacted back. Every
            // reader bounds itself on ids.Length, so trailing slack is unreachable rather than
            // searchable. It is only worth paying the copy when a model change has left enough of
            // the table at the wrong width for the slack itself to be the bigger cost.
            var slack = (long)(total - rows) * stride;
            if (slack > data.Length / 8)
            {
                Array.Resize(ref data, rows * stride);
            }
        }

        logger.LogInformation(
            "Built the search vector index: {Rows} series × {Dim} dims ({Mb:F0} MB) in {Elapsed:F1}s" +
            "{Stale}",
            rows, dimensions, rows * (double)stride / (1024 * 1024), (DateTime.UtcNow - started).TotalSeconds,
            mismatched > 0 ? $"; skipped {mismatched} vector(s) from an older model" : string.Empty);

        var authorsById = new string[authorIds.Count];
        foreach (var (name, id) in authorIds)
        {
            authorsById[id] = name;
        }

        var parts = new VectorIndexParts(
            ids,
            data,
            scales,
            dimensions,
            new VectorIndexColumns(
                years, ratings, chapters, typeIdx, statusIdx,
                JaggedInts.From(genreIdx), JaggedInts.From(authorIdx), JaggedInts.From(artistIdx),
                popularity, new PackedTags(tagBytes.WrittenSpan.ToArray(), tagOffsets),
                contentRatingIdx, [], startDays),
            typeIds, statusIds, genreIds, authorsById, contentRatingIds, LoadTaste(ids));
        return (Assemble(parts, conn), parts, key);
    }

    /// <summary>
    /// The index over <paramref name="parts"/>, with the pieces that are read live whichever way
    /// the parts arrived: the tag vocabulary (a few thousand rows) and the franchise components
    /// (deferred to first use). The behavioural layer travels in the parts, so it is mapped from the
    /// snapshot too; the snapshot key carries the taste file's stamp for that reason.
    /// </summary>
    private VectorIndex Assemble(VectorIndexParts parts, SqliteConnection conn)
    {
        var tagVocabulary = ReadTagVocabulary(conn);
        var ids = parts.Ids;
        return new VectorIndex(
            ids,
            parts.Data,
            parts.Scales,
            parts.Dimensions,
            parts.Columns,
            new VectorIndexVocabularies(
                parts.Types, parts.Statuses, parts.Genres, new NameMap(parts.AuthorsById), tagVocabulary.Tags,
                parts.ContentRatings, tagVocabulary.Subtrees),
            parts.Taste,
            franchiseLoader: () => LoadFranchises(ids));
    }

    /// <summary>
    /// What a snapshot has to have been written under to stand in for a build: this assembly, the
    /// dump file, the vector database's content (<see cref="EmbeddingStore.ContentVersionKey"/>,
    /// or the file itself when nothing has written one yet, which is a downloaded artifact nothing
    /// has modified), and the model.
    /// </summary>
    private string SnapshotKey(SqliteConnection conn, long dumpTicks, long dumpLength)
    {
        var content = ContentStamp(conn);
        var tastePath = tasteOptions?.DatabasePath;
        var taste = !string.IsNullOrWhiteSpace(tastePath) && new FileInfo(tastePath) is { Exists: true } tasteFile
            ? $"taste:{tasteFile.LastWriteTimeUtc.Ticks}:{tasteFile.Length}"
            : "taste:none";

        return string.Join('|',
            SnapshotFile.BuildIdentity, $"{dumpTicks}:{dumpLength}", content, StoredModelVersion(conn) ?? "-",
            options.ModelVersion, options.Dimensions.ToString(CultureInfo.InvariantCulture), taste);
    }

    private string ContentStamp(SqliteConnection conn)
    {
        string? content = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM main.meta WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", EmbeddingStore.ContentVersionKey);
            try
            {
                content = cmd.ExecuteScalar() as string;
            }
            catch (SqliteException)
            {
            }
        }

        if (content is null)
        {
            var file = new FileInfo(options.VectorDbPath);
            content = $"file:{file.LastWriteTimeUtc.Ticks}:{file.Length}";
        }

        return content;
    }

    private VectorIndexParts? TryReadSnapshot(string key)
    {
        try
        {
            return VectorIndexSnapshot.TryRead(SnapshotPath, key);
        }
        catch (Exception ex)
        {
            // A damaged or half-written file only costs this load its shortcut.
            logger.LogInformation(ex, "Ignoring an unreadable search vector snapshot; rebuilding");
            return null;
        }
    }

    /// <summary>
    /// Writes the snapshot off the request path, then swaps the published index for one reading the
    /// snapshot in place, so the built copy on the heap can be collected. A build that an
    /// invalidation outdated while it was being written deletes its own file, so the next load
    /// cannot read what the invalidation meant to throw away.
    /// </summary>
    private void SaveSnapshot(VectorIndex built, VectorIndexParts parts, string key, long generation)
    {
        SnapshotSavedForTest = Task.Run(() =>
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                VectorIndexSnapshot.Write(SnapshotPath, key, parts);
                lock (_publish)
                {
                    if (generation != _generation)
                    {
                        DeleteSnapshot();
                        return;
                    }
                }

                logger.LogDebug("Saved the search vector snapshot in {Elapsed:F1}s", clock.Elapsed.TotalSeconds);
                // A heap reread (mapping off or unavailable) would only be a second copy.
                if (TryReadSnapshot(key) is not { Ids.IsMapped: true } mapped)
                {
                    return;
                }

                lock (_publish)
                {
                    // The exact index these parts built, not merely one of the same generation: a
                    // dump swap rebuilds without bumping it, and this snapshot is the older dump's.
                    if (generation == _generation && _loaded is { } current
                        && ReferenceEquals(current.Index, built))
                    {
                        _loaded = current with { Index = Rehome(current.Index, mapped) };
                    }
                }
            }
            catch (Exception ex)
            {
                // Only costs the next reload its shortcut.
                logger.LogInformation(ex, "Could not save the search vector snapshot");
            }
        });
    }

    /// <summary>
    /// <paramref name="built"/> over the mapped copy of its own parts, keeping everything that was
    /// read live: vocabularies, the behavioural layer, and the franchise components, loaded or not.
    /// </summary>
    private VectorIndex Rehome(VectorIndex built, VectorIndexParts mapped)
    {
        return new VectorIndex(
            mapped.Ids, mapped.Data, mapped.Scales, mapped.Dimensions, mapped.Columns, built.Vocabularies,
            mapped.Taste ?? built.Taste, franchiseLoader: built.SharedFranchises);
    }

    /// <summary>
    /// Same-work components, projected onto this index's rows on first FranchiseAt access. Warmup
    /// still skips this scan; it is paid by whichever request first asks for a component, which in
    /// practice is the first recommendation pool built after an index load. Only rows carrying a
    /// relation edge are read, so it is a fraction of the dump rather than another full pass, and
    /// the Lazy behind it means the request after that pays nothing. Unions still cover every id
    /// the dump mentions, including unembedded connecting volumes.
    /// </summary>
    private int[] LoadFranchises(Column<long> ids)
    {
        var franchise = new int[ids.Length];
        Array.Fill(franchise, VectorIndex.Unknown);

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // The vector build's connection is already disposed. Open the current dump only for
            // this scan, so an unused lazy graph holds no file handle across a dump replacement.
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dumpOptions.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            conn.Open();

            // Stored when the dump was installed; building it here parses every relation blob in
            // the file, 3 to 7 s cold. Same components and ids either way.
            var componentOf = FranchiseGraph.Read(conn) ?? FranchiseGraph.Build(conn);
            var covered = 0;
            for (var row = 0; row < ids.Length; row++)
            {
                if (componentOf.TryGetValue(ids[row], out var component))
                {
                    franchise[row] = component;
                    covered++;
                }
            }

            logger.LogInformation(
                "Franchise graph: {Covered} of {Rows} rows in a same-work component ({Elapsed:F1}s)",
                covered, ids.Length, clock.Elapsed.TotalSeconds);
        }
        catch (SqliteException ex)
        {
            // A dump too old to carry the relationship columns leaves every row franchise-less,
            // which is exactly how the recommender behaved before this existed.
            logger.LogWarning(ex, "Could not build the franchise graph; collapse is off this build");
        }
        finally
        {
            // A pass over the series table, a large share of the dump. Only covered before when the
            // index load came from Discover, whose rail batches drop the file afterwards.
            PageCache.DropAfterScan(dumpOptions.DatabasePath);
        }

        return franchise;
    }

    /// <summary>
    /// Projects the behavioural artifact onto this index's rows, or returns null when it is absent,
    /// which is the normal state of an install that has never downloaded one.
    ///
    /// <para>
    /// Loaded here, at index-build time, rather than kept in a cache of its own: the scan needs a
    /// vector per ROW and a dictionary lookup per candidate per query would cost more than the dot
    /// product it feeds. The price is that installing the artifact has to invalidate the index, the
    /// same way an indexing pass does.
    /// </para>
    /// </summary>
    private TasteLayer? LoadTaste(Column<long> ids)
    {
        var path = tasteOptions?.DatabasePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();

            int dimensions;
            using (var meta = conn.CreateCommand())
            {
                meta.CommandText = "SELECT value FROM meta WHERE key = 'dimensions'";
                if (meta.ExecuteScalar()?.ToString() is not { Length: > 0 } value
                    || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out dimensions)
                    || dimensions <= 0)
                {
                    logger.LogWarning("Taste vectors at {Path} declare no usable dimension; ignored", path);
                    return null;
                }
            }

            var rowById = new Dictionary<long, int>(ids.Length);
            for (var i = 0; i < ids.Length; i++)
            {
                rowById[ids[i]] = i;
            }

            // Sized for every artifact row, then trimmed to the rows this index holds. The artifact
            // covers about 95k series and the index about 64k of them.
            long artifactRows;
            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM item_vectors";
                artifactRows = Math.Min(ids.Length, Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture));
            }

            var data = new sbyte[artifactRows * dimensions <= int.MaxValue
                ? artifactRows * dimensions
                : throw new InvalidOperationException("taste layer too large")];
            var scales = new float[artifactRows];
            var slots = new int[ids.Length];
            Array.Fill(slots, -1);
            var covered = 0;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, scale, vec FROM item_vectors";
            cmd.CommandTimeout = 300;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!rowById.TryGetValue(reader.GetInt64(0), out var row))
                {
                    // Covered by the artifact but not by this index: a novel, an inactive row, or
                    // one that never embedded. Dropped rather than counted.
                    continue;
                }

                if (reader.GetValue(2) is not byte[] blob || blob.Length != dimensions)
                {
                    continue;
                }

                var scale = (float)reader.GetDouble(1);
                if (scale == 0 || slots[row] >= 0 || covered >= artifactRows)
                {
                    // Scale 0 was this layer's "absent" marker, so a row can never store one.
                    continue;
                }

                Buffer.BlockCopy(blob, 0, data, covered * dimensions, dimensions);
                scales[covered] = scale;
                slots[row] = covered++;
            }

            if (covered < artifactRows)
            {
                Array.Resize(ref data, covered * dimensions);
                Array.Resize(ref scales, covered);
            }

            logger.LogInformation(
                "Loaded behavioural vectors: {Covered} of {Rows} rows ({Percent:P0}), {Dim} dims",
                covered, ids.Length, ids.Length == 0 ? 0 : (double)covered / ids.Length, dimensions);

            return covered == 0 ? null : new TasteLayer(slots, data, scales, dimensions);
        }
        catch (Exception ex)
        {
            // Any failure here costs only the behavioural channel; it must never take the whole index with it.
            logger.LogWarning(ex, "Could not read taste vectors at {Path}; the channel stays off", path);
            return null;
        }
    }

    /// <summary>
    /// Tag name → the vocabulary ids carrying it, so a tag filter is an integer test against the
    /// packed blobs rather than a name lookup per row. Several ids per name is normal: the
    /// vocabulary interns casing variants separately.
    ///
    /// Empty on an index written before tag_vocab existed, which reads as "no tag matches anything"
    /// — the honest answer for a filter whose vocabulary isn't there, and the next indexing pass
    /// writes it.
    /// </summary>
    private static (IReadOnlyDictionary<string, int[]> Tags, IReadOnlyDictionary<string, int[]> Subtrees)
        ReadTagVocabulary(SqliteConnection conn)
    {
        var entries = new List<(int Id, string Name, string Path)>();
        // An index built before name_path existed still filters by name; it just has no subtags.
        foreach (var sql in (string[])["SELECT id, name, name_path FROM tag_vocab", "SELECT id, name, '' FROM tag_vocab"])
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (GetString(reader, 1) is { } name && !string.IsNullOrWhiteSpace(name))
                    {
                        entries.Add((reader.GetInt32(0), name, GetString(reader, 2) ?? string.Empty));
                    }
                }

                break;
            }
            catch (SqliteException)
            {
                entries.Clear();
            }
        }

        return (
            entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToArray(), StringComparer.OrdinalIgnoreCase),
            TagSubtrees.Build(entries));
    }

    /// <summary>Maps a low-cardinality column value to a byte id, growing the vocabulary as it goes.</summary>
    private static byte Intern(Dictionary<string, byte> vocab, string? value)
    {
        var key = value ?? string.Empty;
        if (vocab.TryGetValue(key, out var id))
        {
            return id;
        }

        // 255 distinct types/statuses would mean the dump changed shape entirely; bucket the
        // overflow rather than throwing, so search still works on a weird dump.
        if (vocab.Count >= byte.MaxValue)
        {
            return byte.MaxValue;
        }

        id = (byte)vocab.Count;
        vocab[key] = id;
        return id;
    }

    /// <summary>
    /// Reads one of the dump's JSON string arrays (genres, authors) into interned ids, growing the
    /// vocabulary as it goes.
    /// </summary>
    private static int[] ParseNames(
        string? json, Dictionary<string, int> vocab, bool ignoreSentinels = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<string>? names;
        try
        {
            names = JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return [];
        }

        if (names is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<int>(names.Count);
        foreach (var name in names)
        {
            if (ignoreSentinels && !CreditNames.IsPerson(name))
            {
                continue;
            }

            if (!vocab.TryGetValue(name, out var id))
            {
                id = vocab.Count;
                vocab[name] = id;
            }

            result.Add(id);
        }

        return [.. result];
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>total_chapters is TEXT and may be fractional (see the dump notes).</summary>
    private static bool HasDumpColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA dump.table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>published_start_date</c> as a day number. The dump writes <c>yyyy-MM-dd</c>, but a partial
    /// <c>yyyy-MM</c> or <c>yyyy</c> reads as the start of that month or year rather than as unknown.
    /// </summary>
    internal static int? ParseStartDay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length > 10)
        {
            text = text[..10];
        }

        string[] formats = ["yyyy-MM-dd", "yyyy-MM", "yyyy"];
        return DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.DayNumber
            : null;
    }

    private static int? ParseCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac)
            ? (int)frac
            : null;
    }
}
