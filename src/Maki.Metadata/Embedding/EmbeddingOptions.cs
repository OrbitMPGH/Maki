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
    /// <summary>
    /// The active model. Settable so a model switch can take effect live (see
    /// <c>EmbeddingModelSwitcher</c>): reassigning it repoints every derived member below —
    /// dimensionality, model files, prebuilt tag — without rebuilding the DI graph or restarting.
    /// Reassignment is a single reference write; callers read it fresh each time.
    /// </summary>
    public EmbeddingModelProfile Model { get; set; } = Model;

    /// <summary>
    /// False when the user turned embeddings off entirely (the "off" model). Gates every embedding
    /// path: the embedder won't load, the prebuilt installer no-ops, and search/recommendations
    /// report not-ready and fall back to lexical/genre. Mutable so switching to/from "off" is live.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Embedding dimensionality — 768 for the base model. Follows <see cref="Model"/>.</summary>
    public int Dimensions => Model.Dimensions;

    /// <summary>Descriptions are truncated to this many tokens before embedding.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// The execution provider to run the session on. Comes from the environment (see
    /// <see cref="EmbeddingRuntime"/>) and defaults to what every install runs, CPU — it exists for
    /// the machine that builds the published index, not for users. The <c>init</c> setter is for
    /// tests, which must not depend on ambient environment state.
    /// </summary>
    public EmbeddingProvider Provider { get; init; } =
        EmbeddingRuntime.ResolveProvider(Environment.GetEnvironmentVariable(EmbeddingRuntime.ProviderVariable));

    /// <summary>
    /// Which precision of the export to download and run. Derived from <see cref="Provider"/> when
    /// unset, which is what keeps a CUDA run off the int8 graph (see
    /// <see cref="EmbeddingRuntime.ResolvePrecision"/>). Computed rather than defaulted in an
    /// initializer because a property initializer would capture the pre-<c>init</c> provider.
    /// </summary>
    public EmbeddingPrecision Precision =>
        PrecisionOverride ??
        EmbeddingRuntime.ResolvePrecision(Environment.GetEnvironmentVariable(EmbeddingRuntime.PrecisionVariable), Provider);

    /// <summary>How many texts go through one forward pass. Defaults follow <see cref="Provider"/>.</summary>
    public int BatchSize =>
        BatchSizeOverride ??
        EmbeddingRuntime.ResolveBatchSize(Environment.GetEnvironmentVariable(EmbeddingRuntime.BatchSizeVariable), Provider);

    /// <summary>Test seams: set these instead of mutating process environment variables.</summary>
    public EmbeddingPrecision? PrecisionOverride { get; init; }

    public int? BatchSizeOverride { get; init; }

    /// <summary>
    /// Part of every stored vector's content hash, so a change (new model, or a change to the
    /// embedded-text formula) invalidates the index and forces a one-time re-embed. Until it
    /// finishes, the table holds both widths; readers keep only rows matching <see cref="Dimensions"/>
    /// (see <see cref="VectorIndexCache"/>), and search falls back to the title index meanwhile.
    /// </summary>
    public string ModelVersion => Model.Version;

    /// <summary>Each model installs in its own folder, so switching doesn't overwrite the other.</summary>
    public string ModelDirectory => Path.Combine(ModelsRootDirectory, Model.FolderName);

    /// <summary>
    /// Named for the precision, so int8 and fp32 copies of the same model coexist in one folder
    /// rather than the store finding a stale file at a shared path and skipping the download.
    /// Int8 keeps the bare <c>model.onnx</c>, so no existing install re-downloads anything.
    /// </summary>
    /// <summary>
    /// An external-data graph cannot be renamed. The .onnx records its weights file by literal name
    /// ("model.onnx_data") and ONNX Runtime resolves that relative to the .onnx, so saving the graph
    /// under a precision-suffixed name would leave it hunting for a companion that isn't there. Such
    /// models therefore keep their upstream file name, and get one folder per model anyway, so two
    /// precisions of the same one simply cannot coexist - which is fine, since nothing ships them.
    /// </summary>
    public string ModelFileName =>
        Model.ModelDataUrl is null
            ? EmbeddingRuntime.LocalFileName(Precision)
            : Path.GetFileName(new Uri(Model.ModelUrlFor(Precision)).AbsolutePath);

    public string VocabFileName { get; init; } = "vocab.txt";

    // Int8-quantized ONNX by default: a quarter of fp32's size and several times faster on CPU,
    // with negligible ranking-quality loss. The GPU build overrides the precision, not this URL.
    public string ModelUrl =>
        Environment.GetEnvironmentVariable("MAKI_EMBED_MODEL_URL") ?? Model.ModelUrlFor(Precision);

    public string VocabUrl =>
        Environment.GetEnvironmentVariable("MAKI_EMBED_VOCAB_URL") ?? Model.VocabUrl;

    public string ModelPath => Path.Combine(ModelDirectory, ModelFileName);

    /// <summary>
    /// Companion weights for an external-data graph. The name is NOT free: ONNX Runtime reads the
    /// location recorded inside the .onnx, which for these exports is the bare "<model>.onnx_data"
    /// next to it, so the download has to land under exactly that name.
    /// </summary>
    public string ModelDataPath => Model.ModelDataUrlFor(Precision) is { } url
        ? Path.Combine(ModelDirectory, Path.GetFileName(new Uri(url).AbsolutePath))
        : Path.Combine(ModelDirectory, ModelFileName + "_data");

    /// <summary>Byte-level BPE merges, alongside the vocab. Unused by the WordPiece models.</summary>
    public string MergesPath => Path.Combine(ModelDirectory, "merges.txt");
    public string VocabPath => Path.Combine(ModelDirectory, VocabFileName);
}
