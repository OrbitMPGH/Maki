namespace Maki.Metadata.Embedding;

/// <summary>
/// Locations and parameters for the local text-embedding model
/// (bge-small-en-v1.5 — a 384-dim BERT). Constructed from AppPaths in the API host;
/// URLs are env-overridable so tests can point at a local file server.
/// </summary>
public record EmbeddingOptions(string ModelDirectory, string VectorDbPath, string StagingDirectory)
{
    /// <summary>Embedding dimensionality (bge-small = 384).</summary>
    public int Dimensions { get; init; } = 384;

    /// <summary>Descriptions are truncated to this many tokens before embedding.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// Bumped whenever the model or the text-building formula changes, so stored vectors
    /// are treated as stale and re-embedded. Part of every stored row's hash.
    /// </summary>
    // q2: themes (weighted tags_v2) joined the embedded text — forces the one-time re-embed.
    public const string ModelVersion = "bge-small-en-v1.5-q2";

    public string ModelFileName { get; init; } = "model.onnx";
    public string VocabFileName { get; init; } = "vocab.txt";

    // Int8-quantized ONNX: ~34 MB and several times faster on CPU than fp32, with negligible
    // similarity-ranking quality loss (cos separation is essentially unchanged).
    public string ModelUrl { get; init; } =
        Environment.GetEnvironmentVariable("MAKI_EMBED_MODEL_URL")
        ?? "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/onnx/model_quantized.onnx";

    public string VocabUrl { get; init; } =
        Environment.GetEnvironmentVariable("MAKI_EMBED_VOCAB_URL")
        ?? "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main/vocab.txt";

    public string ModelPath => Path.Combine(ModelDirectory, ModelFileName);
    public string VocabPath => Path.Combine(ModelDirectory, VocabFileName);
}
