using Maki.Metadata.Embedding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

/// <summary>
/// A model file of plausible size that will not parse is deleted so the next load downloads it
/// again. That used to happen once per process, so a second model never got the same treatment.
/// </summary>
public class TextEmbedderCorruptModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maki-corrupt-model-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private (TextEmbedder Embedder, EmbeddingOptions Options) CorruptModel(string name)
    {
        var options = new EmbeddingOptions(Path.Combine(_root, name), "", "", EmbeddingModelProfile.Base);
        Directory.CreateDirectory(options.ModelDirectory);

        var garbage = new byte[21_000_000];
        new Random(1).NextBytes(garbage);
        File.WriteAllBytes(options.ModelPath, garbage);

        var vocab = new List<string> { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" };
        vocab.AddRange(Enumerable.Range(0, 12_000).Select(i => $"word{i}"));
        File.WriteAllLines(options.VocabPath, vocab);

        var store = new EmbeddingModelStore(new FakeDumpHttpClientFactory([]), options, NullLogger<EmbeddingModelStore>.Instance);
        return (new TextEmbedder(options, store, NullLogger<TextEmbedder>.Instance), options);
    }

    [Fact]
    public async Task Each_corrupt_model_is_deleted_not_only_the_first()
    {
        var (first, firstOptions) = CorruptModel("one");
        var (second, secondOptions) = CorruptModel("two");
        using (first)
        using (second)
        {
            Assert.False(await first.EnsureReadyAsync());
            Assert.False(await second.EnsureReadyAsync());
        }

        Assert.False(File.Exists(firstOptions.ModelPath));
        Assert.False(File.Exists(secondOptions.ModelPath));
    }
}
