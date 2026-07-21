using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata.Embedding;

/// <summary>A tag's display/scoring metadata from the vocabulary table.</summary>
public sealed record TagInfo(string Name, long SeriesCount, bool IsSpoiler);

/// <summary>
/// Persists one embedding vector per MangaBaka series in its own SQLite file, separate from
/// the nightly-swapped dump so vectors survive dump refreshes. Each row carries a content
/// hash so the indexer only re-embeds series whose text (or the model) changed. Also holds
/// the packed weighted-tag blobs (<see cref="TagMath"/>) and the tag vocabulary.
/// </summary>
public class EmbeddingStore(EmbeddingOptions options)
{
    public string DbPath => options.VectorDbPath;

    private bool _schemaEnsured;

    public void EnsureSchema()
    {
        if (_schemaEnsured)
        {
            return;
        }

        using var conn = OpenWritable();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS series_vectors (
                    id    INTEGER PRIMARY KEY,
                    hash  TEXT NOT NULL,
                    scale REAL NOT NULL,
                    vec   BLOB NOT NULL
                );
                CREATE TABLE IF NOT EXISTS series_tags (
                    id   INTEGER PRIMARY KEY,
                    tags BLOB NOT NULL
                );
                CREATE TABLE IF NOT EXISTS tag_vocab (
                    id           INTEGER PRIMARY KEY,
                    name         TEXT NOT NULL,
                    series_count INTEGER NOT NULL,
                    is_spoiler   INTEGER NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        MigrateFloat32Vectors(conn);
        _schemaEnsured = true;
    }

    /// <summary>
    /// Converts a pre-int8 database in place. Quantization is pure arithmetic on vectors we
    /// already hold, so this costs a pass over the table (seconds) rather than a re-embed
    /// (~an hour) — which is why the storage change doesn't bump <see cref="EmbeddingOptions.ModelVersion"/>.
    /// No-op once the <c>scale</c> column exists.
    /// </summary>
    private static void MigrateFloat32Vectors(SqliteConnection conn)
    {
        var hasScale = false;
        using (var columns = conn.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(series_vectors)";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "scale", StringComparison.OrdinalIgnoreCase))
                {
                    hasScale = true;
                    break;
                }
            }
        }

        if (hasScale)
        {
            return;
        }

        using (var addColumn = conn.CreateCommand())
        {
            addColumn.CommandText = "ALTER TABLE series_vectors ADD COLUMN scale REAL NOT NULL DEFAULT 0";
            addColumn.ExecuteNonQuery();
        }

        // Ids first, then convert in batches. Holding every float32 vector at once would spike
        // ~300 MB on a full catalogue, which is a lot to ask of the small boxes this runs on.
        var ids = new List<long>();
        using (var idCmd = conn.CreateCommand())
        {
            idCmd.CommandText = "SELECT id FROM series_vectors";
            idCmd.CommandTimeout = 600;
            using var reader = idCmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        const int batchSize = 2000;
        var packed = Array.Empty<sbyte>();
        for (var offset = 0; offset < ids.Count; offset += batchSize)
        {
            var batch = ids.GetRange(offset, Math.Min(batchSize, ids.Count - offset));
            var converted = new List<(long Id, float Scale, byte[] Vec)>(batch.Count);

            using (var read = conn.CreateCommand())
            {
                read.CommandText = $"SELECT id, vec FROM series_vectors WHERE id IN ({string.Join(",", batch)})";
                read.CommandTimeout = 600;
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetValue(1) is not byte[] blob || EmbeddingMath.FromBlob(blob) is not { } vec)
                    {
                        continue;
                    }

                    if (packed.Length != vec.Length)
                    {
                        packed = new sbyte[vec.Length];
                    }

                    converted.Add((reader.GetInt64(0), EmbeddingMath.Quantize(vec, packed), ToBytes(packed)));
                }
            }

            using var tx = conn.BeginTransaction();
            using (var write = conn.CreateCommand())
            {
                write.Transaction = tx;
                write.CommandText = "UPDATE series_vectors SET scale = $scale, vec = $vec WHERE id = $id";
                var pScale = write.Parameters.Add("$scale", SqliteType.Real);
                var pVec = write.Parameters.Add("$vec", SqliteType.Blob);
                var pId = write.Parameters.Add("$id", SqliteType.Integer);
                foreach (var (id, scale, vec) in converted)
                {
                    pScale.Value = scale;
                    pVec.Value = vec;
                    pId.Value = id;
                    write.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        // Shrinking a row in place doesn't return the space: the pages stay allocated to the
        // table with the freed bytes stranded inside them, so without this the whole point of
        // the conversion (~400 MB -> ~90 MB) is lost. One-time cost on the one-time migration.
        using var vacuum = conn.CreateCommand();
        vacuum.CommandText = "VACUUM";
        vacuum.CommandTimeout = 1800;
        vacuum.ExecuteNonQuery();
    }

    public int Count()
    {
        if (!File.Exists(DbPath))
        {
            return 0;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM series_vectors";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>id → stored content hash, for skip-unchanged during indexing.</summary>
    public Dictionary<long, string> GetHashes()
    {
        var map = new Dictionary<long, string>();
        if (!File.Exists(DbPath))
        {
            return map;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, hash FROM series_vectors";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt64(0)] = reader.GetString(1);
        }

        return map;
    }

    public void UpsertBatch(IReadOnlyList<(long Id, string Hash, float[] Vector)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using var conn = OpenWritable();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR REPLACE INTO series_vectors (id, hash, scale, vec) VALUES ($id, $hash, $scale, $vec)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var pScale = cmd.Parameters.Add("$scale", SqliteType.Real);
        var pVec = cmd.Parameters.Add("$vec", SqliteType.Blob);
        var packed = Array.Empty<sbyte>();
        foreach (var (id, hash, vector) in rows)
        {
            if (packed.Length != vector.Length)
            {
                packed = new sbyte[vector.Length];
            }

            pId.Value = id;
            pHash.Value = hash;
            pScale.Value = EmbeddingMath.Quantize(vector, packed);
            pVec.Value = ToBytes(packed);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Ids that already have a stored tag blob, for skip-unchanged during indexing.</summary>
    public HashSet<long> GetTaggedIds()
    {
        var ids = new HashSet<long>();
        if (!File.Exists(DbPath))
        {
            return ids;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM series_tags";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public void UpsertTagsBatch(IReadOnlyList<(long Id, byte[] Tags)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using var conn = OpenWritable();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO series_tags (id, tags) VALUES ($id, $tags)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pTags = cmd.Parameters.Add("$tags", SqliteType.Blob);
        foreach (var (id, tags) in rows)
        {
            pId.Value = id;
            pTags.Value = tags;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void UpsertVocab(IReadOnlyDictionary<int, TagInfo> vocab)
    {
        if (vocab.Count == 0)
        {
            return;
        }

        using var conn = OpenWritable();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO tag_vocab (id, name, series_count, is_spoiler)
            VALUES ($id, $name, $count, $spoiler)
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
        var pSpoiler = cmd.Parameters.Add("$spoiler", SqliteType.Integer);
        foreach (var (id, info) in vocab)
        {
            pId.Value = id;
            pName.Value = info.Name;
            pCount.Value = info.SeriesCount;
            pSpoiler.Value = info.IsSpoiler ? 1 : 0;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>id → packed tag blob for the given ids (skips ids without stored tags).</summary>
    public Dictionary<long, byte[]> GetTagBlobs(IReadOnlyCollection<long> ids)
    {
        var map = new Dictionary<long, byte[]>();
        if (ids.Count == 0 || !File.Exists(DbPath))
        {
            return map;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, tags FROM series_tags WHERE id IN ({string.Join(",", ids)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(1) is byte[] blob)
            {
                map[reader.GetInt64(0)] = blob;
            }
        }

        return map;
    }

    /// <summary>The full tag vocabulary (~3k rows) — names, spoiler flags, and IDF counts.</summary>
    public Dictionary<int, TagInfo> GetVocab()
    {
        var map = new Dictionary<int, TagInfo>();
        if (!File.Exists(DbPath))
        {
            return map;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, series_count, is_spoiler FROM tag_vocab";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt32(0)] = new TagInfo(
                reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3) != 0);
        }

        return map;
    }

    public float[]? GetVector(long id)
    {
        if (!File.Exists(DbPath))
        {
            return null;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scale, vec FROM series_vectors WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var single = cmd.ExecuteReader();
        return single.Read() ? Dequantize(single, scaleOrdinal: 0, vecOrdinal: 1) : null;
    }

    /// <summary>id → vector for the given ids (skips ids without a stored vector).</summary>
    public Dictionary<long, float[]> GetVectors(IReadOnlyCollection<long> ids)
    {
        var map = new Dictionary<long, float[]>();
        if (ids.Count == 0 || !File.Exists(DbPath))
        {
            return map;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, scale, vec FROM series_vectors WHERE id IN ({string.Join(",", ids)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Dequantize(reader, scaleOrdinal: 1, vecOrdinal: 2) is { } vec)
            {
                map[reader.GetInt64(0)] = vec;
            }
        }

        return map;
    }

    /// <summary>Unit-mean of the vectors for the given ids (skips ids without a vector); null if none found.</summary>
    public float[]? GetMeanVector(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0 || !File.Exists(DbPath))
        {
            return null;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT scale, vec FROM series_vectors WHERE id IN ({string.Join(",", ids)})";
        var vectors = new List<float[]>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Dequantize(reader, scaleOrdinal: 0, vecOrdinal: 1) is { } vec)
            {
                vectors.Add(vec);
            }
        }

        return EmbeddingMath.Mean(vectors);
    }

    /// <summary>
    /// Weighted-mean seed vector: each id contributes in proportion to its weight (ids missing from
    /// <paramref name="weights"/> default to 1.0). Used to bias the seed vector toward the titles
    /// the user rated highly. Skips ids without a stored vector; null if none found.
    /// </summary>
    public float[]? GetMeanVector(IReadOnlyCollection<long> ids, IReadOnlyDictionary<long, double>? weights)
    {
        if (weights is null || weights.Count == 0)
        {
            return GetMeanVector(ids);
        }

        if (ids.Count == 0 || !File.Exists(DbPath))
        {
            return null;
        }

        using var conn = OpenReadOnly();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, scale, vec FROM series_vectors WHERE id IN ({string.Join(",", ids)})";
        var weighted = new List<(float[] Vec, double Weight)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Dequantize(reader, scaleOrdinal: 1, vecOrdinal: 2) is { } vec)
            {
                weighted.Add((vec, weights.GetValueOrDefault(reader.GetInt64(0), 1.0)));
            }
        }

        return EmbeddingMath.WeightedMean(weighted);
    }

    /// <summary>
    /// Drops vector/tag rows for series that are no longer candidates. Vectors outlive the
    /// nightly dump swap by design, so a series that stops qualifying (state flips to merged,
    /// rating cleared, re-rated pornographic) would otherwise keep its row forever — inflating
    /// the stored count past the recommendable total. Only safe after a full pass, where
    /// <paramref name="keepIds"/> is every current candidate.
    /// </summary>
    public int PruneExcept(IReadOnlyCollection<long> keepIds)
    {
        if (keepIds.Count == 0 || !File.Exists(DbPath))
        {
            return 0;
        }

        using var conn = OpenWritable();
        using var tx = conn.BeginTransaction();

        using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "CREATE TEMP TABLE keep_ids (id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
        }

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO keep_ids (id) VALUES ($id)";
            var pId = insert.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in keepIds)
            {
                pId.Value = id;
                insert.ExecuteNonQuery();
            }
        }

        int removed;
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM series_vectors WHERE id NOT IN (SELECT id FROM keep_ids)";
            removed = delete.ExecuteNonQuery();

            delete.CommandText = "DELETE FROM series_tags WHERE id NOT IN (SELECT id FROM keep_ids)";
            delete.ExecuteNonQuery();
        }

        using (var drop = conn.CreateCommand())
        {
            drop.Transaction = tx;
            drop.CommandText = "DROP TABLE keep_ids";
            drop.ExecuteNonQuery();
        }

        tx.Commit();
        return removed;
    }

    /// <summary>
    /// Reads one int8 row back as floats. Vectors are stored quantized (a quarter of float32's
    /// size, and what the search index wants anyway); callers still see <c>float[]</c>, so the
    /// only visible effect is that a round-trip is accurate to ~3 decimals rather than exact.
    /// </summary>
    private static float[]? Dequantize(SqliteDataReader reader, int scaleOrdinal, int vecOrdinal)
    {
        if (reader.IsDBNull(scaleOrdinal) || reader.GetValue(vecOrdinal) is not byte[] blob)
        {
            return null;
        }

        var scale = (float)reader.GetDouble(scaleOrdinal);
        var vec = new float[blob.Length];
        for (var i = 0; i < blob.Length; i++)
        {
            vec[i] = (sbyte)blob[i] * scale;
        }

        return vec;
    }

    /// <summary>Reinterprets the packed int8 vector as bytes for BLOB storage (no copy of meaning, just of bits).</summary>
    private static byte[] ToBytes(sbyte[] packed) => MemoryMarshal.AsBytes(packed.AsSpan()).ToArray();

    private SqliteConnection OpenWritable()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        return conn;
    }

    private SqliteConnection OpenReadOnly()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }
}
