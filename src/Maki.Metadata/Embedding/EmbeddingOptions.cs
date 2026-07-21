namespace Maki.Metadata.Embedding;

/// <summary>
/// Locations and parameters for the local text-embedding model
/// (bge-base-en-v1.5 — a 768-dim BERT). Constructed from AppPaths in the API host;
/// URLs are env-overridable so tests can point at a local file server.
/// </summary>
public record EmbeddingOptions(string ModelDirectory, string VectorDbPath, string StagingDirectory)
{
    /// <summary>
    /// Names the model's folder under {ConfigDir}/models, so switching models installs
    /// alongside the old files rather than over them.
    /// </summary>
    public const string ModelName = "bge-base-en-v1.5";

    /// <summary>Embedding dimensionality (bge-base = 768).</summary>
    public int Dimensions { get; init; } = 768;

    /// <summary>Descriptions are truncated to this many tokens before embedding.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// Bumped whenever the model or the text-building formula changes, so stored vectors
    /// are treated as stale and re-embedded. Part of every stored row's hash.
    /// </summary>
    // q2: themes (weighted tags_v2) joined the embedded text — forces the one-time re-embed.
    // q3: bge-small → bge-base. Measured on a 12k-series pool against a fixed query set, base
    // lifts MRR 0.318 → 0.393 and recall@10 from 6/12 to 8/12 (a "wandering swordsman in feudal
    // Japan" query finds Vagabond at #151 instead of #618). Costs ~2× the indexing time, ~2× the
    // stored vectors, and ~2× the search index's memory.
    //
    // Until the re-embed finishes, the table holds both widths; readers keep only the rows
    // matching Dimensions (see VectorIndexCache), and search falls back to the title index while
    // too few current-width vectors exist.
    public const string ModelVersion = "bge-base-en-v1.5-q3";

    public string ModelFileName { get; init; } = "model.onnx";
    public string VocabFileName { get; init; } = "vocab.txt";

    // Int8-quantized ONNX: ~110 MB and several times faster on CPU than fp32, with negligible
    // similarity-ranking quality loss (cos separation is essentially unchanged).
    public string ModelUrl { get; init; } =
        Environment.GetEnvironmentVariable("MAKI_EMBED_MODEL_URL")
        ?? "https://huggingface.co/Xenova/bge-base-en-v1.5/resolve/main/onnx/model_quantized.onnx";

    public string VocabUrl { get; init; } =
        Environment.GetEnvironmentVariable("MAKI_EMBED_VOCAB_URL")
        ?? "https://huggingface.co/BAAI/bge-base-en-v1.5/resolve/main/vocab.txt";

    public string ModelPath => Path.Combine(ModelDirectory, ModelFileName);
    public string VocabPath => Path.Combine(ModelDirectory, VocabFileName);
}
