namespace Maki.Metadata.Embedding;

/// <summary>Which ONNX Runtime execution provider the embedding session asks for.</summary>
public enum EmbeddingProvider
{
    /// <summary>What every Maki install runs. Works with the CPU-only ONNX Runtime package.</summary>
    Cpu,

    /// <summary>
    /// CUDA. Needs the GPU build of ONNX Runtime (<c>MakiOnnxGpu=true</c> at build time) plus a
    /// CUDA 13.x runtime and cuDNN 9.x on the machine; without them the append call throws and
    /// <see cref="TextEmbedder"/> logs and falls back to CPU rather than failing the pass.
    /// </summary>
    Cuda,
}

/// <summary>
/// Which precision of the model's ONNX export to download and run.
///
/// There is no fp16 member, and that is a finding rather than an omission. Xenova publishes an
/// fp16 export beside the other two, and ONNX Runtime 1.27 cannot load it at all — it fails inside
/// graph optimization, before any execution provider is chosen, with
/// <c>"Attempting to get index by a name which does not exist:InsertedPrecisionFreeCast_… for node:
/// /embeddings/LayerNorm/Mul/SimplifiedLayerNormFusion/"</c>. Since that is the optimizer and not a
/// missing kernel, a GPU would hit it too. Don't add the member back without loading the file first.
/// </summary>
public enum EmbeddingPrecision
{
    /// <summary>The shipped default: a quarter of fp32's size and several times faster on CPU.</summary>
    Int8,

    /// <summary>The unquantized export. The GPU choice: full-speed CUDA kernels, float output.</summary>
    Fp32,
}

/// <summary>
/// The two knobs that pick <em>how</em> embeddings are computed, as opposed to <em>which</em> model
/// computes them (that is <see cref="EmbeddingModelProfile"/>, and it is a user setting).
///
/// Environment variables rather than settings, deliberately. They exist for the one machine that
/// builds the published index (<c>distribution/publish-embeddings.ps1 -Cuda</c>), and every value
/// but the default needs software the shipped container does not have. A switch in the Settings UI
/// would offer every user a control that can only fail for them.
///
/// Precision is <em>not</em> part of <see cref="EmbeddingOptions.ModelVersion"/>, and that omission
/// is the point. The same weights are being run either way, so a GPU-built fp32 index stays
/// interchangeable with a CPU-built int8 one and can be published to CPU clients. Folding precision
/// into the version would invalidate every downloaded artifact and make each user re-embed the whole
/// catalogue, which is the exact cost the prebuilt artifact exists to avoid. The trade is that the
/// vectors are no longer bit-identical across precisions and nothing detects the difference
/// automatically, so measure a precision change on the fixed query set before publishing one.
/// </summary>
public static class EmbeddingRuntime
{
    public const string ProviderVariable = "MAKI_EMBED_PROVIDER";
    public const string PrecisionVariable = "MAKI_EMBED_PRECISION";
    public const string BatchSizeVariable = "MAKI_EMBED_BATCH";

    /// <summary>Batches on CPU: small enough that a stalled pass is interruptible, big enough to amortize.</summary>
    public const int DefaultCpuBatchSize = 32;

    /// <summary>A GPU is bandwidth-bound at 32 and idles between forward passes; 128 keeps it fed.</summary>
    public const int DefaultCudaBatchSize = 128;

    public static EmbeddingProvider ResolveProvider(string? value) =>
        string.Equals(value?.Trim(), "cuda", StringComparison.OrdinalIgnoreCase)
            ? EmbeddingProvider.Cuda
            : EmbeddingProvider.Cpu;

    /// <summary>
    /// The precision to run at. Unset follows the provider: int8 on CPU (what every install has
    /// already downloaded), fp32 on CUDA. The CUDA default is not a preference. Measured on an
    /// RTX 5080 over 2,000 real passages: CPU int8 39 rows/s, CUDA int8 31, CUDA fp32 281. ONNX
    /// Runtime cannot keep a quantized graph on the device, so CUDA over int8 is *slower than the
    /// CPU*. An explicit int8 is still honoured under CUDA rather than rejected, because that is how
    /// the measurement gets re-taken after an ONNX Runtime upgrade.
    /// </summary>
    public static EmbeddingPrecision ResolvePrecision(string? value, EmbeddingProvider provider) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "int8" or "quantized" => EmbeddingPrecision.Int8,
            "fp32" or "float32" => EmbeddingPrecision.Fp32,
            _ => provider == EmbeddingProvider.Cuda ? EmbeddingPrecision.Fp32 : EmbeddingPrecision.Int8,
        };

    /// <summary>Batch size, defaulted from the provider. A non-numeric or non-positive value is ignored.</summary>
    public static int ResolveBatchSize(string? value, EmbeddingProvider provider) =>
        int.TryParse(value?.Trim(), out var n) && n > 0
            ? n
            : provider == EmbeddingProvider.Cuda ? DefaultCudaBatchSize : DefaultCpuBatchSize;

    /// <summary>
    /// The on-disk name for a precision. Int8 keeps the bare <c>model.onnx</c> it has always had, so
    /// an existing install is never made to re-download; the others take a suffix, which is also what
    /// stops a precision switch from silently reusing the file already sitting in the model folder
    /// (<see cref="EmbeddingModelStore"/> skips the download when the path is present and big enough).
    /// </summary>
    public static string LocalFileName(EmbeddingPrecision precision) =>
        precision == EmbeddingPrecision.Fp32 ? "model.fp32.onnx" : "model.onnx";

    /// <summary>
    /// The file name inside the upstream export's <c>onnx/</c> folder. Note this is not
    /// <see cref="LocalFileName"/>: upstream, the unquantized graph is the one called
    /// <c>model.onnx</c>, while locally that name belongs to int8 for the back-compat reason above.
    /// </summary>
    public static string UpstreamFileName(EmbeddingPrecision precision) =>
        precision == EmbeddingPrecision.Fp32 ? "model.onnx" : "model_quantized.onnx";
}
