namespace Maki.Metadata.Embedding;

/// <summary>
/// Locations and parameters for the local text-embedding model. The model itself is chosen by
/// <see cref="Model"/> (see <see cref="EmbeddingModelProfile"/>); this record adds the on-disk
/// paths and the tokenizer limits. Constructed from AppPaths in the API host, with the profile
/// resolved from the user's setting. URLs are env-overridable so tests can point at a local server.
/// </summary>
public record EmbeddingOptions(
    string ModelsRootDirectory, string VectorDbPath, string StagingDirectory, EmbeddingModelProfile Model)
{
    /// <summary>Embedding dimensionality — 768 (base) or 1024 (large).</summary>
    public int Dimensions { get; init; } = Model.Dimensions;

    /// <summary>Descriptions are truncated to this many tokens before embedding.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// Part of every stored vector's content hash, so a change (new model, or a change to the
    /// embedded-text formula) invalidates the index and forces a one-time re-embed. Until it
    /// finishes, the table holds both widths; readers keep only rows matching <see cref="Dimensions"/>
    /// (see <see cref="VectorIndexCache"/>), and search falls back to the title index meanwhile.
    /// </summary>
    public string ModelVersion => Model.Version;

    /// <summary>Each model installs in its own folder, so switching doesn't overwrite the other.</summary>
    public string ModelDirectory => Path.Combine(ModelsRootDirectory, Model.FolderName);

    public string ModelFileName { get; init; } = "model.onnx";
    public string VocabFileName { get; init; } = "vocab.txt";

    // Int8-quantized ONNX: a quarter of fp32's size and several times faster on CPU, with
    // negligible ranking-quality loss.
    public string ModelUrl =>
        Environment.GetEnvironmentVariable("MAKI_EMBED_MODEL_URL") ?? Model.ModelUrl;

    public string VocabUrl =>
        Environment.GetEnvironmentVariable("MAKI_EMBED_VOCAB_URL") ?? Model.VocabUrl;

    public string ModelPath => Path.Combine(ModelDirectory, ModelFileName);
    public string VocabPath => Path.Combine(ModelDirectory, VocabFileName);
}
