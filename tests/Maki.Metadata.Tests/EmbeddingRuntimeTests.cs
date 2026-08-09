using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

public class EmbeddingRuntimeTests
{
    [Theory]
    [InlineData("cuda", EmbeddingProvider.Cuda)]
    [InlineData("CUDA", EmbeddingProvider.Cuda)]
    [InlineData(" cuda ", EmbeddingProvider.Cuda)]
    [InlineData("cpu", EmbeddingProvider.Cpu)]
    [InlineData("rocm", EmbeddingProvider.Cpu)]
    [InlineData("", EmbeddingProvider.Cpu)]
    [InlineData(null, EmbeddingProvider.Cpu)]
    public void ResolveProvider_AnythingButCuda_IsCpu(string? value, EmbeddingProvider expected) =>
        Assert.Equal(expected, EmbeddingRuntime.ResolveProvider(value));

    /// <summary>
    /// The load-bearing default: CUDA over a quantized graph runs mostly on the CPU anyway, so an
    /// unset precision must not leave a GPU build pointed at the int8 export.
    /// </summary>
    [Fact]
    public void ResolvePrecision_Unset_FollowsTheProvider()
    {
        Assert.Equal(EmbeddingPrecision.Int8, EmbeddingRuntime.ResolvePrecision(null, EmbeddingProvider.Cpu));
        Assert.Equal(EmbeddingPrecision.Fp32, EmbeddingRuntime.ResolvePrecision(null, EmbeddingProvider.Cuda));
    }

    [Theory]
    [InlineData("int8", EmbeddingPrecision.Int8)]
    [InlineData("quantized", EmbeddingPrecision.Int8)]
    [InlineData("INT8", EmbeddingPrecision.Int8)]
    public void ResolvePrecision_Explicit_OverridesTheProviderDefault(string value, EmbeddingPrecision expected) =>
        Assert.Equal(expected, EmbeddingRuntime.ResolvePrecision(value, EmbeddingProvider.Cuda));

    /// <summary>
    /// fp16 is not a member (ONNX Runtime cannot load the export). An old value in the environment
    /// must not resolve to something arbitrary — it falls back to the provider's default, which for
    /// CUDA is the precision that actually works.
    /// </summary>
    [Fact]
    public void ResolvePrecision_Fp16_FallsBackRatherThanResolving() =>
        Assert.Equal(EmbeddingPrecision.Fp32, EmbeddingRuntime.ResolvePrecision("fp16", EmbeddingProvider.Cuda));

    [Theory]
    [InlineData(null, EmbeddingProvider.Cpu, EmbeddingRuntime.DefaultCpuBatchSize)]
    [InlineData(null, EmbeddingProvider.Cuda, EmbeddingRuntime.DefaultCudaBatchSize)]
    [InlineData("nonsense", EmbeddingProvider.Cpu, EmbeddingRuntime.DefaultCpuBatchSize)]
    [InlineData("0", EmbeddingProvider.Cpu, EmbeddingRuntime.DefaultCpuBatchSize)]
    [InlineData("-8", EmbeddingProvider.Cpu, EmbeddingRuntime.DefaultCpuBatchSize)]
    [InlineData("256", EmbeddingProvider.Cpu, 256)]
    public void ResolveBatchSize_RejectsNonPositiveAndUnparsable(string? value, EmbeddingProvider provider, int expected) =>
        Assert.Equal(expected, EmbeddingRuntime.ResolveBatchSize(value, provider));

    /// <summary>
    /// Int8 must keep the bare name it has always had on disk, or every existing install
    /// re-downloads its model on upgrade for no reason.
    /// </summary>
    [Fact]
    public void LocalFileName_Int8_IsUnsuffixed() =>
        Assert.Equal("model.onnx", EmbeddingRuntime.LocalFileName(EmbeddingPrecision.Int8));

    [Fact]
    public void LocalFileName_IsDistinctPerPrecision()
    {
        var names = Enum.GetValues<EmbeddingPrecision>().Select(EmbeddingRuntime.LocalFileName).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Theory]
    [InlineData(EmbeddingPrecision.Int8, "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/onnx/model_quantized.onnx")]
    [InlineData(EmbeddingPrecision.Fp32, "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/onnx/model.onnx")]
    public void ModelUrlFor_SwapsOnlyTheFinalSegment(EmbeddingPrecision precision, string expected) =>
        Assert.Equal(expected, EmbeddingModelProfile.Base.ModelUrlFor(precision));

    /// <summary>
    /// Pooling and the two prefixes were added so mean-pooled, differently-prefixed models could be
    /// evaluated. Their defaults must reproduce exactly what the shipped models already did, or the
    /// addition silently re-embeds every catalogue: the passage prefix is inside the content hash,
    /// and a pooling change alters every vector.
    ///
    /// Still true after base moved from bge-base to snowflake-arctic-embed-m: arctic-embed is also a
    /// CLS-pooled encoder and was trained with the same "Represent this sentence…" query instruction,
    /// which is a large part of why that swap needed no code change at all. Verified against the
    /// model's own 1_Pooling/config.json and config_sentence_transformers.json, not assumed.
    /// </summary>
    [Theory]
    [InlineData("base")]
    public void ShippedModels_KeepClsPoolingAndTheInstructionPrefixes(string kind)
    {
        var profile = EmbeddingModelProfile.Resolve(kind);
        Assert.Equal(EmbeddingPooling.Cls, profile.Pooling);
        Assert.Equal("Represent this sentence for searching relevant passages: ", profile.QueryPrefix);
        Assert.Equal(string.Empty, profile.PassagePrefix);
    }

    [Fact]
    public void ModelUrlFor_Int8_IsTheProfileUrlVerbatim()
    {
        Assert.Equal(EmbeddingModelProfile.Base.ModelUrl, EmbeddingModelProfile.Base.ModelUrlFor(EmbeddingPrecision.Int8));
    }
}

public class EmbeddingOptionsRuntimeTests
{
    private static EmbeddingOptions Options(EmbeddingProvider provider) =>
        new("models", "vectors.db", "staging", EmbeddingModelProfile.Base) { Provider = provider };

    /// <summary>
    /// Nothing about a default-constructed options changes for an existing install: same file name,
    /// same URL, same batch size as before the provider knob existed.
    /// </summary>
    [Fact]
    public void Cpu_IsUnchangedFromBeforeTheGpuPath()
    {
        var options = Options(EmbeddingProvider.Cpu);
        Assert.Equal(EmbeddingPrecision.Int8, options.Precision);
        Assert.Equal("model.onnx", options.ModelFileName);
        Assert.Equal(EmbeddingModelProfile.Base.ModelUrl, options.ModelUrl);
        Assert.Equal(32, options.BatchSize);
    }

    /// <summary>
    /// The two must move together: a GPU run that kept the int8 file name would find the CPU model
    /// already on disk, skip the download, and quietly run the graph CUDA cannot accelerate.
    /// </summary>
    [Fact]
    public void Cuda_TakesTheFp32FileAndUrl()
    {
        var options = Options(EmbeddingProvider.Cuda);
        Assert.Equal(EmbeddingPrecision.Fp32, options.Precision);
        Assert.Equal("model.fp32.onnx", options.ModelFileName);
        Assert.EndsWith("/onnx/model.onnx", options.ModelUrl, StringComparison.Ordinal);
        Assert.NotEqual(Options(EmbeddingProvider.Cpu).ModelPath, options.ModelPath);
    }

    [Fact]
    public void Overrides_BeatTheProviderDefaults()
    {
        var options = Options(EmbeddingProvider.Cuda) with
        {
            PrecisionOverride = EmbeddingPrecision.Int8,
            BatchSizeOverride = 7,
        };
        Assert.Equal("model.onnx", options.ModelFileName);
        Assert.Equal(7, options.BatchSize);
    }
}
