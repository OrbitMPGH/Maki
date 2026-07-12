using Microsoft.Data.Sqlite;

namespace Mangarr.Metadata.Embedding;

/// <summary>
/// Persists one embedding vector per MangaBaka series in its own SQLite file, separate from
/// the nightly-swapped dump so vectors survive dump refreshes. Each row carries a content
/// hash so the indexer only re-embeds series whose text (or the model) changed.
/// </summary>
public class EmbeddingStore(EmbeddingOptions options)
{
    public string DbPath => options.VectorDbPath;

    public void EnsureSchema()
    {
        using var conn = OpenWritable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS series_vectors (
                id   INTEGER PRIMARY KEY,
                hash TEXT NOT NULL,
                vec  BLOB NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
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
