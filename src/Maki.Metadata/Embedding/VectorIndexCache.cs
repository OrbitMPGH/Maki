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
/// both DBs (seconds), so it happens once and is only dropped when the embedding index is rebuilt
/// — call <see cref="Invalidate"/> after an indexing pass.
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
    private volatile VectorIndex? _index;

    /// <summary>Drops the cached index so the next search rebuilds it. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        _index = null;
        logger.LogDebug("Search vector index invalidated");
    }

    /// <summary>
    /// Replaces the vector database with <paramref name="stagedPath"/> and drops the cached index.
    /// Runs under the build lock so a swap can never race a build that is midway through reading
    /// the old file. The WAL sidecars belong to the file being replaced, so they go with it —
    /// leaving them would let SQLite reconstruct pages of the *previous* database over the new one.
    /// </summary>
    public async Task SwapDatabaseAsync(string stagedPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _index = null;
            SqliteConnection.ClearAllPools();

            foreach (var sidecar in new[] { options.VectorDbPath + "-wal", options.VectorDbPath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Move(stagedPath, options.VectorDbPath, overwrite: true);
            logger.LogInformation("Swapped in a new vector database at {Path}", options.VectorDbPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The index, building it if needed. Null when there's nothing to search — no vector DB, no
    /// dump, or an index that hasn't been built yet.
    /// </summary>
    public async Task<VectorIndex?> GetAsync(CancellationToken ct = default)
    {
        if (_index is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_index is { } raced)
            {
                return raced;
            }

            if (!File.Exists(options.VectorDbPath) || !File.Exists(dumpOptions.DatabasePath))
            {
                return null;
            }

            _index = await Task.Run(() => Build(ct), ct);
            return _index;
        }
        finally
        {
            _lock.Release();
        }
    }

    private VectorIndex? Build(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        using var conn = new SqliteConnection($"Data Source={options.VectorDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $dump AS dump";
            attach.Parameters.AddWithValue("$dump", dumpOptions.DatabasePath);
            attach.ExecuteNonQuery();
        }

        // Sized up front so the vectors land in one flat array instead of a growing list of
        // 120k small ones. Costs one extra scan on a build that already scans everything.
        int total;
        using (var count = conn.CreateCommand())
        {
            count.CommandText =
                $"SELECT COUNT(*) FROM series_vectors v JOIN dump.series d ON d.id = v.id WHERE {CandidateWhere}";
            count.CommandTimeout = 600;
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (total == 0)
        {
            logger.LogInformation("Search vector index empty — nothing embedded yet");
            return null;
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
        var popularity = new int[total];
        var tagBlobs = new byte[]?[total];
        var contentRatingIdx = new byte[total];

        // The configured model's dimensionality is authoritative, not whatever the first row
        // happens to be: after a model change the table holds both old and new vectors until the
        // re-embed finishes, and inferring the width from row one would throw away whichever
        // generation didn't come first.
        var dimensions = options.Dimensions;
        var cells = (long)total * dimensions;
        if (cells > int.MaxValue)
        {
            logger.LogWarning("Vector index too large to hold in memory ({Cells} cells)", cells);
            return null;
        }

        var data = new sbyte[cells];
        var mismatched = 0;

        var typeIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var statusIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var genreIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var contentRatingIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Authors are far higher-cardinality than the other vocabularies (tens of thousands of
        // names against a few hundred genres), which is a few MB of strings — worth it, because it
        // is what lets the recommender answer its author-match term without touching SQLite.
        var authorIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var rows = 0;
        using (var scan = conn.CreateCommand())
        {
            scan.CommandText = $"""
                SELECT v.id, v.scale, v.vec, d.year, d.rating, d.total_chapters, d.type, d.status,
                       d.genres, t.tags, d.authors, d.popularity_global_current, d.content_rating
                FROM series_vectors v
                LEFT JOIN series_tags t ON t.id = v.id
                JOIN dump.series d ON d.id = v.id
                WHERE {CandidateWhere}
                """;
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
                scales[rows] = (float)reader.GetDouble(1);
                blob.CopyTo(MemoryMarshal.AsBytes(data.AsSpan(rows * dimensions, dimensions)));
                years[rows] = reader.IsDBNull(3) ? VectorIndex.Unknown : reader.GetInt32(3);
                ratings[rows] = (float)reader.GetDouble(4);
                chapters[rows] = ParseCount(GetString(reader, 5)) ?? VectorIndex.Unknown;
                typeIdx[rows] = Intern(typeIds, GetString(reader, 6));
                statusIdx[rows] = Intern(statusIds, GetString(reader, 7));
                genreIdx[rows] = ParseNames(GetString(reader, 8), genreIds);
                // Packed tag blobs ride along so the tag channel can score the whole catalogue
                // instead of only re-ranking what the dense pass already found (~20 MB).
                tagBlobs[rows] = reader.GetValue(9) as byte[];
                authorIdx[rows] = ParseNames(GetString(reader, 10), authorIds);
                popularity[rows] = reader.IsDBNull(11) ? VectorIndex.Unknown : reader.GetInt32(11);
                contentRatingIdx[rows] = Intern(contentRatingIds, GetString(reader, 12));
                rows++;
            }
        }

        if (rows == 0)
        {
            logger.LogInformation(
                "Search vector index empty — {Mismatched} stored vector(s) are the wrong width for the " +
                "configured model; they'll be re-embedded on the next indexing pass",
                mismatched);
            return null;
        }

        // The count query and the scan can disagree (a skipped row, a concurrent dump swap);
        // trim to what was actually read so no zeroed rows are searchable.
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
            Array.Resize(ref popularity, rows);
            Array.Resize(ref tagBlobs, rows);
            Array.Resize(ref contentRatingIdx, rows);
            Array.Resize(ref data, rows * dimensions);
        }

        logger.LogInformation(
            "Built the search vector index: {Rows} series × {Dim} dims ({Mb:F0} MB) in {Elapsed:F1}s" +
            "{Stale}",
            rows, dimensions, rows * (double)dimensions / (1024 * 1024), (DateTime.UtcNow - started).TotalSeconds,
            mismatched > 0 ? $"; skipped {mismatched} vector(s) from an older model" : string.Empty);

        return new VectorIndex(
            ids,
            data,
            scales,
            dimensions,
            new VectorIndexColumns(
                years, ratings, chapters, typeIdx, statusIdx, genreIdx, authorIdx, popularity, tagBlobs,
                contentRatingIdx),
            new VectorIndexVocabularies(
                typeIds, statusIds, genreIds, authorIds, ReadTagVocabulary(conn), contentRatingIds),
            LoadTaste(ids));
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
    private TasteLayer? LoadTaste(long[] ids)
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

            var data = new sbyte[(long)ids.Length * dimensions <= int.MaxValue
                ? ids.Length * dimensions
                : throw new InvalidOperationException("taste layer too large")];
            var scales = new float[ids.Length];
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

                var blob = (byte[])reader["vec"];
                if (blob.Length != dimensions)
                {
                    continue;
                }

                var scale = (float)reader.GetDouble(1);
                if (scale == 0)
                {
                    // Scale 0 is this layer's "absent" marker, so a row can never store one.
                    continue;
                }

                Buffer.BlockCopy(blob, 0, data, row * dimensions, dimensions);
                scales[row] = scale;
                covered++;
            }

            logger.LogInformation(
                "Loaded behavioural vectors: {Covered} of {Rows} rows ({Percent:P0}), {Dim} dims",
                covered, ids.Length, ids.Length == 0 ? 0 : (double)covered / ids.Length, dimensions);

            return covered == 0 ? null : new TasteLayer(data, scales, dimensions, covered);
        }
        catch (SqliteException ex)
        {
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
    private static IReadOnlyDictionary<string, int[]> ReadTagVocabulary(SqliteConnection conn)
    {
        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name FROM tag_vocab";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = GetString(reader, 1);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (!byName.TryGetValue(name, out var ids))
                    {
                        byName[name] = ids = [];
                    }

                    ids.Add(reader.GetInt32(0));
                }
            }
        }
        catch (SqliteException)
        {
            return new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        }

        return byName.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
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
    private static int[] ParseNames(string? json, Dictionary<string, int> vocab)
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

        var result = new int[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            if (!vocab.TryGetValue(names[i], out var id))
            {
                id = vocab.Count;
                vocab[names[i]] = id;
            }

            result[i] = id;
        }

        return result;
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>total_chapters is TEXT and may be fractional (see the dump notes).</summary>
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
