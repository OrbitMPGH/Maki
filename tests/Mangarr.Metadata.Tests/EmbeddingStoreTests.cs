using Mangarr.Metadata.Embedding;
using Xunit;

namespace Mangarr.Metadata.Tests;

public class EmbeddingStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly EmbeddingStore _store;

    public EmbeddingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangarr-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var options = new EmbeddingOptions(_dir, Path.Combine(_dir, "embeddings.db"), _dir);
        _store = new EmbeddingStore(options);
        _store.EnsureSchema();
    }

    [Fact]
    public void Upsert_ThenReadVector_RoundTrips()
    {
        _store.UpsertBatch([(7L, "h7", [0.1f, 0.2f, 0.3f])]);
        Assert.Equal([0.1f, 0.2f, 0.3f], _store.GetVector(7));
        Assert.Null(_store.GetVector(999));
    }

    [Fact]
    public void Upsert_ReplacesExistingRow()
    {
        _store.UpsertBatch([(7L, "h7", [1f, 0f])]);
        _store.UpsertBatch([(7L, "h7b", [0f, 1f])]);
        Assert.Equal([0f, 1f], _store.GetVector(7));
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
