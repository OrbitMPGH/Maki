using Maki.Metadata.Embedding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

/// <summary>
/// The shipped model through <see cref="TextEmbedder"/>, plain against mapped. Needs the real
/// model, which tests cannot download: set <c>MAKI_TEST_EMBED_MODEL_DIR</c> to a folder holding the
/// base profile's <c>model.onnx</c> and <c>vocab.txt</c>. Without it the test does nothing.
/// </summary>
public class TextEmbedderMappedWeightsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maki-mapped-embedder-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly string[] Texts =
    [
        "berserk", "one piece", "a cooking manga set in a fantasy world", "romance between childhood friends",
        "isekai with a villainess", "dark fantasy revenge", "sports manga about volleyball", "slice of life cats",
        "time travel mystery", "post apocalyptic survival", "school comedy", "vampire hunter",
        "historical samurai drama", "mecha war", "idol industry", "detective solves murders",
        "reincarnated as a slime", "magic academy", "workplace romance office", "horror body transformation",
        "sad story about grief", "martial arts tournament", "space opera", "chess prodigy",
        "girls band music", "zombie outbreak high school", "cyberpunk hacker", "boxing underdog",
        "ghost stories folklore", "mahjong gambling", "a dragon raising a child", "farming in another world",
        "tower climbing dungeon", "yakuza family comedy", "witch delivery service", "baseball high school",
        "psychological thriller game of death", "medical drama surgeon", "food delivery", "もう一度",
        string.Join(' ', Enumerable.Repeat(
            "A young swordsman travels across a ruined continent searching for the demon that destroyed his village, " +
            "meeting companions, losing friends and slowly learning that the war he was raised to fight was a lie.", 8)),
        string.Join(' ', Enumerable.Repeat(
            "Two coworkers at a small publishing house fall in love over late nights spent fixing typos and arguing " +
            "about which manuscripts deserve a chance, while the company itself struggles to stay afloat.", 6)),
        "",
    ];

    [Fact]
    public async Task Mapped_weights_embed_bit_identically_to_the_plain_model()
    {
        var source = Environment.GetEnvironmentVariable("MAKI_TEST_EMBED_MODEL_DIR");
        if (string.IsNullOrEmpty(source) || !File.Exists(Path.Combine(source, "model.onnx")))
        {
            return;
        }

        var plainOptions = Options("plain", source, mapWeights: false);
        float[][] plain, plainBatch;
        using (var embedder = Embedder(plainOptions))
        {
            Assert.True(await embedder.EnsureReadyAsync());
            Assert.False(embedder.WeightsMapped);
            plain = Texts.Select(embedder.Embed).ToArray();
            plainBatch = embedder.EmbedBatch(Texts);
        }

        Assert.False(File.Exists(new MappedEmbeddingModel(plainOptions.ModelPath).GraphPath));

        var mappedOptions = Options("mapped", source, mapWeights: true);
        float[][] mapped, mappedBatch;
        using (var embedder = Embedder(mappedOptions))
        {
            Assert.True(await embedder.EnsureReadyAsync());
            Assert.False(embedder.WeightsMapped);
            await embedder.MappedBuildForTest;
            Assert.True(new MappedEmbeddingModel(mappedOptions.ModelPath).IsCurrent());

            embedder.Reset();
            Assert.True(await embedder.EnsureReadyAsync());
            Assert.True(embedder.WeightsMapped);
            mapped = Texts.Select(embedder.Embed).ToArray();

            mappedBatch = embedder.EmbedBatch(Texts);
        }

        for (var i = 0; i < Texts.Length; i++)
        {
            Assert.Equal(plain[i], mapped[i]);
            Assert.Equal(plainBatch[i], mappedBatch[i]);
        }

        // A data file cut short on disk is rebuilt by the next load, which runs on the plain model meanwhile.
        var pair = new MappedEmbeddingModel(mappedOptions.ModelPath);
        using (var data = new FileStream(pair.DataPath, FileMode.Open))
        {
            data.SetLength(data.Length / 3);
        }

        using (var embedder = Embedder(mappedOptions))
        {
            Assert.True(await embedder.EnsureReadyAsync());
            Assert.False(embedder.WeightsMapped);
            Assert.Equal(plain[0], embedder.Embed(Texts[0]));
            await embedder.MappedBuildForTest;
            Assert.True(pair.IsCurrent());
        }
    }

    private EmbeddingOptions Options(string name, string source, bool mapWeights)
    {
        var options = new EmbeddingOptions(Path.Combine(_root, name), "", "", EmbeddingModelProfile.Base)
        {
            MapWeights = mapWeights,
            Provider = EmbeddingProvider.Cpu,
            PrecisionOverride = EmbeddingPrecision.Int8,
        };
        Directory.CreateDirectory(options.ModelDirectory);
        File.Copy(Path.Combine(source, "model.onnx"), options.ModelPath);
        File.Copy(Path.Combine(source, "vocab.txt"), options.VocabPath);
        return options;
    }

    private static TextEmbedder Embedder(EmbeddingOptions options) =>
        new(options, new EmbeddingModelStore(new FakeDumpHttpClientFactory([]), options, NullLogger<EmbeddingModelStore>.Instance),
            NullLogger<TextEmbedder>.Instance);
}
