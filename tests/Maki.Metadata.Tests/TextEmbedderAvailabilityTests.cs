using Maki.Metadata.Embedding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

/// <summary>
/// Discover waits on the semantic engine for a narrowed query only while the model can load. An
/// offline instance whose model download fails must say so, or every such search answers empty.
/// </summary>
public class TextEmbedderAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maki-embedder-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TextEmbedder Embedder(bool enabled = true)
    {
        var options = new EmbeddingOptions(_root, "", "", EmbeddingModelProfile.Base) { Enabled = enabled };
        var store = new EmbeddingModelStore(
            new FakeDumpHttpClientFactory([]), options, NullLogger<EmbeddingModelStore>.Instance);
        return new TextEmbedder(options, store, NullLogger<TextEmbedder>.Instance);
    }

    [Fact]
    public async Task A_model_that_failed_to_load_cannot_load_until_its_backoff_ends()
    {
        using var embedder = Embedder();
        Assert.True(embedder.CanLoad);

        Assert.False(await embedder.EnsureReadyAsync());

        Assert.False(embedder.CanLoad);
    }

    [Fact]
    public void Embeddings_turned_off_cannot_load()
    {
        using var embedder = Embedder(enabled: false);
        Assert.False(embedder.CanLoad);
    }
}
