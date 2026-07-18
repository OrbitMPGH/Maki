using Microsoft.Data.Sqlite;

namespace Mangarr.Metadata.Embedding;

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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS series_vectors (
                id   INTEGER PRIMARY KEY,
                hash TEXT NOT NULL,
                vec  BLOB NOT NULL
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
        _schemaEnsured = true;
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
        cmd.CommandText = "INSERT OR REPLACE INTO series_vectors (id, hash, vec) VALUES ($id, $hash, $vec)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var pVec = cmd.Parameters.Add("$vec", SqliteType.Blob);
        foreach (var (id, hash, vector) in rows)
        {
            pId.Value = id;
            pHash.Value = hash;
            pVec.Value = EmbeddingMath.ToBlob(vector);
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
        cmd.CommandText = "SELECT vec FROM series_vectors WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is byte[] blob ? EmbeddingMath.FromBlob(blob) : null;
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
        cmd.CommandText = $"SELECT id, vec FROM series_vectors WHERE id IN ({string.Join(",", ids)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(1) is byte[] blob && EmbeddingMath.FromBlob(blob) is { } vec)
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
        cmd.CommandText = $"SELECT vec FROM series_vectors WHERE id IN ({string.Join(",", ids)})";
        var vectors = new List<float[]>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(0) is byte[] blob && EmbeddingMath.FromBlob(blob) is { } vec)
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
        cmd.CommandText = $"SELECT id, vec FROM series_vectors WHERE id IN ({string.Join(",", ids)})";
        var weighted = new List<(float[] Vec, double Weight)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(1) is byte[] blob && EmbeddingMath.FromBlob(blob) is { } vec)
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
