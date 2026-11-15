using Maki.Metadata.Embedding;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

public class EmbeddingStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly EmbeddingStore _store;

    public EmbeddingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var options = new EmbeddingOptions(_dir, Path.Combine(_dir, "embeddings.db"), _dir, EmbeddingModelProfile.Base);
        _store = new EmbeddingStore(options);
        _store.EnsureSchema();
    }

    [Fact]
    public void A_file_records_the_model_its_vectors_came_from()
    {
        // EnsureSchema adopts an unrecorded file as the running model's.
        Assert.Equal(EmbeddingModelProfile.Base.Version, _store.GetModelVersion());

        _store.SetModelVersion("older-model");
        Assert.Equal("older-model", _store.GetModelVersion());

        var other = Path.Combine(_dir, "other.db");
        using (var conn = new SqliteConnection($"Data Source={other};Pooling=False"))
        {
            conn.Open();
        }

        EmbeddingStore.StampModelVersion(other, "artifact-model");
        Assert.Equal("artifact-model", new EmbeddingStore(
            new EmbeddingOptions(_dir, other, _dir, EmbeddingModelProfile.Base)).GetModelVersion());
    }

    [Fact]
    public void ClearTagVectors_DropsEveryNameEmbedding()
    {
        _store.UpsertTagVectors([(1, [1f, 0f, 0f]), (2, [0f, 1f, 0f])]);

        _store.ClearTagVectors();

        Assert.Empty(_store.GetTagVectorIds());
    }

    [Fact]
    public void Upsert_ThenReadVector_RoundTripsWithinQuantizationError()
    {
        // Vectors are stored int8 with a per-row scale, so a round trip is close, not exact.
        _store.UpsertBatch([(7L, "h7", [0.1f, 0.2f, 0.3f])]);

        var vector = _store.GetVector(7);

        Assert.NotNull(vector);
        Assert.Equal(3, vector!.Length);
        Assert.Equal(0.1f, vector[0], 2);
        Assert.Equal(0.2f, vector[1], 2);
        Assert.Equal(0.3f, vector[2], 2);
        Assert.Null(_store.GetVector(999));
    }

    [Fact]
    public void Upsert_ThenReadVector_KeepsDirectionExactlyEnoughForCosine()
    {
        // What actually matters downstream is the angle, not the magnitudes.
        var original = new float[64];
        var random = new Random(99);
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (float)((random.NextDouble() * 2) - 1);
        }

        EmbeddingMath.NormalizeInPlace(original);
        _store.UpsertBatch([(1L, "h", original)]);

        var restored = _store.GetVector(1)!;
        EmbeddingMath.NormalizeInPlace(restored);
        Assert.Equal(1.0f, EmbeddingMath.Cosine(original, restored), 4);
    }

    [Fact]
    public void Upsert_ReplacesExistingRow()
    {
        _store.UpsertBatch([(7L, "h7", [1f, 0f])]);
        _store.UpsertBatch([(7L, "h7b", [0f, 1f])]);
        Assert.Equal([0f, 1f], _store.GetVector(7)!);
        Assert.Equal(1, _store.Count());
        Assert.Equal("h7b", _store.GetHashes()[7]);
    }

    [Fact]
    public void CountAndHashes_ReflectContents()
    {
        _store.UpsertBatch([(1L, "a", [1f, 0f]), (2L, "b", [0f, 1f])]);
        Assert.Equal(2, _store.Count());
        var hashes = _store.GetHashes();
        Assert.Equal("a", hashes[1]);
        Assert.Equal("b", hashes[2]);
    }

    [Fact]
    public void GetMeanVector_AveragesPresentIdsAndSkipsMissing()
    {
        _store.UpsertBatch([(1L, "a", [1f, 0f]), (2L, "b", [0f, 1f])]);
        var mean = _store.GetMeanVector([1, 2, 999]); // 999 has no vector
        Assert.NotNull(mean);
        var inv = 1f / MathF.Sqrt(2f);
        Assert.Equal(inv, mean![0], 3);
        Assert.Equal(inv, mean[1], 3);
    }

    [Fact]
    public void GetMeanVector_NoKnownIds_IsNull() => Assert.Null(_store.GetMeanVector([999]));

    [Fact]
    public void Tags_UpsertAndReadBack_RoundTrip()
    {
        var blob = TagMath.Pack([(1, TagMath.Core), (2, TagMath.Incidental)]);
        _store.UpsertTagsBatch([(7L, blob)]);
        Assert.Equal(blob, _store.GetTagBlobs([7, 999])[7]);
        Assert.Equal([7L], _store.GetTaggedIds());
    }

    [Fact]
    public void Vocab_UpsertAndReadBack_RoundTrip()
    {
        _store.UpsertVocab(new Dictionary<int, TagInfo>
        {
            [1] = new("Time Travel", 800, false),
            [2] = new("Dead Friends", 300, true),
        });
        var vocab = _store.GetVocab();
        Assert.Equal(new TagInfo("Time Travel", 800, false), vocab[1]);
        Assert.True(vocab[2].IsSpoiler);
    }

    [Fact]
    public void EnsureSchema_ConvertsAPreInt8DatabaseInPlace()
    {
        // A database written by the float32 version: no scale column, four bytes per dimension.
        var legacyPath = Path.Combine(_dir, "legacy.db");
        var vector = new float[] { 0.5f, -0.25f, 1f };
        using (var conn = new SqliteConnection($"Data Source={legacyPath};Pooling=False"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE series_vectors (id INTEGER PRIMARY KEY, hash TEXT NOT NULL, vec BLOB NOT NULL);
                """;
            create.ExecuteNonQuery();
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO series_vectors (id, hash, vec) VALUES (1, 'h1', $vec)";
            insert.Parameters.AddWithValue("$vec", EmbeddingMath.ToBlob(vector));
            insert.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var migrated = new EmbeddingStore(new EmbeddingOptions(_dir, legacyPath, _dir, EmbeddingModelProfile.Base));
        migrated.EnsureSchema();

        // The vectors survive the conversion (quantized), and the hash is preserved so the next
        // indexing pass still skips them instead of re-embedding everything.
        var restored = migrated.GetVector(1);
        Assert.NotNull(restored);
        Assert.Equal(0.5f, restored![0], 2);
        Assert.Equal(-0.25f, restored[1], 2);
        Assert.Equal(1f, restored[2], 2);
        Assert.Equal("h1", migrated.GetHashes()[1]);

        // Idempotent: a second call must not re-migrate or corrupt anything.
        var again = new EmbeddingStore(new EmbeddingOptions(_dir, legacyPath, _dir, EmbeddingModelProfile.Base));
        again.EnsureSchema();
        Assert.Equal(1f, again.GetVector(1)![2], 2);
    }

    [Fact]
    public void PruneExcept_DropsVectorsAndTagsOfNonCandidates()
    {
        _store.UpsertBatch([(1L, "a", [1f, 0f]), (2L, "b", [0f, 1f]), (3L, "c", [1f, 1f])]);
        _store.UpsertTagsBatch([(1L, TagMath.Pack([(5, TagMath.Core)])), (3L, TagMath.Pack([(6, TagMath.Core)]))]);

        Assert.Equal(2, _store.PruneExcept([1L])); // 2 and 3 no longer recommendable

        Assert.Equal(1, _store.Count());
        Assert.NotNull(_store.GetVector(1));
        Assert.Null(_store.GetVector(3));
        Assert.Equal([1L], _store.GetTaggedIds());
    }

    [Fact]
    public void PruneExcept_KeepsEverythingWhenAllStillCandidates()
    {
        _store.UpsertBatch([(1L, "a", [1f, 0f]), (2L, "b", [0f, 1f])]);
        Assert.Equal(0, _store.PruneExcept([1L, 2L, 99L])); // 99 has no row — not an error
        Assert.Equal(2, _store.Count());
    }

    [Fact]
    public void PruneExcept_EmptyKeepSet_IsNoOp()
    {
        // A limited pass must never be able to wipe the store.
        _store.UpsertBatch([(1L, "a", [1f, 0f])]);
        Assert.Equal(0, _store.PruneExcept([]));
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void Count_is_kept_in_meta_through_every_writer()
    {
        Assert.Equal(0, StoredCount(_store.DbPath));

        // New ids, a replaced id, and an id twice in one batch.
        _store.UpsertBatch([(1L, "a", [1f, 0f]), (2L, "b", [0f, 1f])]);
        _store.UpsertBatch([(2L, "b2", [1f, 1f]), (3L, "c", [1f, 0f]), (3L, "c2", [0f, 1f])]);
        Assert.Equal(3, StoredCount(_store.DbPath));

        // Writes that touch no vector row keep it valid under their new content version.
        _store.UpsertTagsBatch([(1L, TagMath.Pack([(5, TagMath.Core)]))]);
        _store.UpsertTagVectors([(1, [1f, 0f])]);
        Assert.Equal(3, StoredCount(_store.DbPath));

        _store.PruneExcept([1L, 3L]);
        Assert.Equal(2, StoredCount(_store.DbPath));
        Assert.Equal(2, RealCount(_store.DbPath));
        Assert.Equal(2, new EmbeddingStore(Options(_store.DbPath)).Count());
    }

    [Fact]
    public void Count_reads_the_stored_value_instead_of_counting()
    {
        _store.UpsertBatch([(1L, "a", [1f, 0f]), (2L, "b", [0f, 1f])]);
        using (var conn = new SqliteConnection($"Data Source={_store.DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE meta SET value = '41:' || (SELECT value FROM meta WHERE key = 'content_version')
                WHERE key = 'vector_count'
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(41, new EmbeddingStore(Options(_store.DbPath)).Count());
    }

    [Fact]
    public void A_file_with_no_stored_count_is_counted_once_and_stamped()
    {
        var path = Path.Combine(_dir, "artifact.db");
        WriteForeignFile(path, rows: 3, contentVersion: null);
        Assert.Null(StoredCount(path));

        Assert.Equal(3, new EmbeddingStore(Options(path)).Count());

        Assert.Equal(3, StoredCount(path));
    }

    [Fact]
    public void A_write_that_does_not_know_the_count_retires_it()
    {
        _store.UpsertBatch([(1L, "a", [1f, 0f])]);
        Assert.Equal(1, StoredCount(_store.DbPath));

        // What an older Maki does: a new row and a new content version, the count untouched.
        using (var conn = new SqliteConnection($"Data Source={_store.DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO series_vectors (id, hash, scale, vec) VALUES (9, 'x', 1, x'0102');
                UPDATE meta SET value = 'older-writer' WHERE key = 'content_version';
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.Null(StoredCount(_store.DbPath));
        Assert.Equal(2, new EmbeddingStore(Options(_store.DbPath)).Count());
        Assert.Equal(2, StoredCount(_store.DbPath));
    }

    [Fact]
    public void A_staged_artifact_carries_its_count()
    {
        var path = Path.Combine(_dir, "staged.db");
        WriteForeignFile(path, rows: 4, contentVersion: "publisher");

        EmbeddingStore.StampVectorCount(path, 4);

        Assert.Equal(4, StoredCount(path));
    }

    private EmbeddingOptions Options(string path) => new(_dir, path, _dir, EmbeddingModelProfile.Base);

    internal static int? StoredCount(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return EmbeddingStore.StoredVectorCount(conn);
    }

    private static int RealCount(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM series_vectors";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>A vector database written by something other than this store, like the published artifact.</summary>
    private static void WriteForeignFile(string path, int rows, string? contentVersion)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series_vectors (id INTEGER PRIMARY KEY, hash TEXT NOT NULL, scale REAL NOT NULL, vec BLOB NOT NULL);
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        for (var i = 1; i <= rows; i++)
        {
            cmd.CommandText = $"INSERT INTO series_vectors VALUES ({i}, 'h', 1, x'0102')";
            cmd.ExecuteNonQuery();
        }

        if (contentVersion is not null)
        {
            cmd.CommandText = $"INSERT INTO meta VALUES ('content_version', '{contentVersion}')";
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
