namespace Maki.Metadata.Embedding;

/// <summary>
/// A selectable embedding model. Maki ships with a lighter default and a heavier, higher-quality
/// option; the choice is a user setting because the cost is paid in the user's RAM and download,
/// not just the index. Measured on a fixed 12-query set (MRR / recall@10, higher better) with the
/// current text formula: base 0.545 / 11-of-12, large 0.639 / 9-of-12 — large sharpens the top
/// hits at ~+260 MB resident and ~+230 MB model download.
///
/// <see cref="Version"/> is part of every stored vector's content hash, so bumping it (a new model,
/// or a change to the embedded-text formula) invalidates the index and forces a one-time re-embed.
/// The two models have different dimensionalities, so switching between them re-embeds regardless.
/// </summary>
public sealed record EmbeddingModelProfile(
    string Kind,
    string FolderName,
    int Dimensions,
    string Version,
    string ModelUrl,
    string VocabUrl,
    string PrebuiltTag)
{
    /// <summary>bge-base-en-v1.5, 768-dim. The default: ~240 MB resident, ~110 MB model download.</summary>
    public static readonly EmbeddingModelProfile Base = new(
        Kind: "base",
        FolderName: "bge-base-en-v1.5",
        Dimensions: 768,
        // q4: dropped the genre/theme facet block from the embedded text and preferred the
        // MangaUpdates description where present (see SeriesEmbeddingIndexer.BuildText). Measured
        // 0.393 → 0.545 MRR on the 12-query set. Forces the one-time re-embed.
        Version: "bge-base-en-v1.5-q4",
        ModelUrl: "https://huggingface.co/Xenova/bge-base-en-v1.5/resolve/main/onnx/model_quantized.onnx",
        VocabUrl: "https://huggingface.co/BAAI/bge-base-en-v1.5/resolve/main/vocab.txt",
        PrebuiltTag: "embeddings-base-latest");

    /// <summary>bge-large-en-v1.5, 1024-dim. Opt-in: ~500 MB resident, ~340 MB model download.</summary>
    public static readonly EmbeddingModelProfile Large = new(
        Kind: "large",
        FolderName: "bge-large-en-v1.5",
        Dimensions: 1024,
        Version: "bge-large-en-v1.5-q4",
        ModelUrl: "https://huggingface.co/Xenova/bge-large-en-v1.5/resolve/main/onnx/model_quantized.onnx",
        VocabUrl: "https://huggingface.co/BAAI/bge-large-en-v1.5/resolve/main/vocab.txt",
        PrebuiltTag: "embeddings-large-latest");

    /// <summary>The configured model; anything but an explicit "large" resolves to the default.</summary>
    public static EmbeddingModelProfile Resolve(string? kind) =>
        string.Equals(kind, "large", StringComparison.OrdinalIgnoreCase) ? Large : Base;
}
