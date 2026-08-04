using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Turns text into a unit-normalized embedding vector using the configured local ONNX model.
/// Tokenize → ONNX forward pass → pool → L2 normalize, where the tokenizer and the pooling both come
/// from <see cref="EmbeddingModelProfile"/> rather than being assumed (the shipped models are
/// WordPiece + CLS, but the eval harness scores byte-level BPE decoders and SentencePiece encoders
/// through this same class).
/// Runs in-process on CPU by default; <see cref="EmbeddingOptions.Provider"/> can ask for CUDA on
/// a machine built against the GPU package, which is how the published index is generated.
/// Thread-safe once initialized.
/// </summary>
public sealed class TextEmbedder(
    EmbeddingOptions options,
    EmbeddingModelStore modelStore,
    ILogger<TextEmbedder> logger) : IDisposable
{
    private const long ClsToken = 101;
    private const long SepToken = 102;

    /// <summary>Qwen's end-of-text id, used only as the stand-in for empty input on a decoder.</summary>
    private const long EosToken = 151643;

    private const string TokenTypeIdsInput = "token_type_ids";

    /// <summary>The pooled output of a graph that pools itself. See <see cref="EmbeddingPooling.Pooled"/>.</summary>
    private const string PooledOutput = "sentence_embedding";

    private const string HiddenStateOutput = "last_hidden_state";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private bool _usesTokenTypeIds;

    public int Dimensions => options.Dimensions;
    public bool IsReady => _session is not null;

    /// <summary>
    /// What the loaded session actually runs on, which is not always what was asked for: a CUDA
    /// request falls back to CPU rather than failing. Null until a session is loaded. The build tool
    /// checks this and refuses to start a pass that would silently take the slow path.
    /// </summary>
    public EmbeddingProvider? ActiveProvider { get; private set; }

    /// <summary>Downloads the model if needed and loads the session/tokenizer. Idempotent.</summary>
    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        // Embeddings turned off: never load a session, so search/recs fall back to lexical/genre.
        if (!options.Enabled)
        {
            return false;
        }

        if (_session is not null)
        {
            return true;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_session is not null)
            {
                return true;
            }

            await modelStore.EnsureAsync(ct);
            _tokenizer = await CreateTokenizerAsync();
            using var sessionOptions = CreateSessionOptions(out var provider);
            _session = new InferenceSession(options.ModelPath, sessionOptions);

            // Read the graph rather than assume it. token_type_ids is a BERT-family input that the
            // Gemma export simply does not declare, and ONNX Runtime rejects a run that feeds an
            // input the graph never asked for just as hard as one that omits a required input.
            _usesTokenTypeIds = _session.InputMetadata.ContainsKey(TokenTypeIdsInput);
            ActiveProvider = provider;
            logger.LogInformation(
                "Text embedder ready ({Dim}-dim, model {Version}, {Precision} on {Provider})",
                Dimensions, options.ModelVersion, options.Precision, provider);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize the text embedder");
            return false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Drops the loaded session and tokenizer so the next <see cref="EnsureReadyAsync"/> reloads
    /// from the currently-configured model. Called after a live model switch, where
    /// <see cref="EmbeddingOptions.Model"/> now points at a different model (and dimensionality)
    /// than the session in memory. Serialized against init so it can't race a concurrent load.
    /// </summary>
    public void Reset()
    {
        _initLock.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
            _tokenizer = null;
            logger.LogInformation("Text embedder reset; will reload on next use");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Session options for the configured provider. A CUDA request that cannot be satisfied — no
    /// GPU build of ONNX Runtime, no CUDA 13.x runtime, no cuDNN 9.x, no visible device — is a
    /// warning and a CPU session, not a failed pass: the whole point of the GPU path is that it
    /// only ever changes how long the same work takes.
    /// </summary>
    private SessionOptions CreateSessionOptions(out EmbeddingProvider provider)
    {
        provider = EmbeddingProvider.Cpu;
        if (options.Provider != EmbeddingProvider.Cuda)
        {
            return new SessionOptions();
        }

        if (options.Precision == EmbeddingPrecision.Int8)
        {
            // Measured on an RTX 5080 over 2,000 real passages: 31 rows/s this way against 39 on the
            // CPU and 281 for fp32 on the same card. ONNX Runtime cannot keep a quantized graph on
            // the device (it inserts ~168 Memcpy nodes), so this combination is slower than not
            // using the GPU at all. Still permitted rather than rejected, because it is the honest
            // way to re-check the claim after an ONNX Runtime upgrade.
            logger.LogWarning(
                "CUDA was requested with the int8 model. Measured slower than plain CPU, because the " +
                "quantized graph cannot stay on the device; set {Variable}=fp32 for the GPU to help.",
                EmbeddingRuntime.PrecisionVariable);
        }

        var sessionOptions = new SessionOptions();
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(0);
            logger.LogInformation("Embedding session using the CUDA execution provider");
            provider = EmbeddingProvider.Cuda;
            return sessionOptions;
        }
        catch (Exception ex)
        {
            // Discard the half-configured options rather than reusing them; the append failed
            // somewhere inside the native call and its state is not ours to reason about.
            sessionOptions.Dispose();

            // Two failures that look alike and are fixed in completely different places. A missing
            // entry point means the CPU onnxruntime.dll was loaded and no amount of CUDA installing
            // will help; anything else means the GPU build is present but could not reach a device.
            var cause = ex is EntryPointNotFoundException or DllNotFoundException
                ? "this is the CPU build of ONNX Runtime, so the binary has no CUDA provider at all. " +
                  "Rebuild with -p:MakiOnnxGpu=true (an environment variable will not do it)."
                : "the GPU build is loaded but no CUDA device was reached. Check that CUDA 13.x and " +
                  "cuDNN 9.x are on PATH and that the driver sees the card.";
            logger.LogWarning(ex, "CUDA execution provider unavailable; falling back to CPU: {Cause}", cause);
            return new SessionOptions();
        }
    }

    /// <summary>
    /// Builds the tokenizer the model's vocabulary needs. Three families, none interchangeable: the
    /// wrong one still produces ids, they just mean nothing, so the model returns confident garbage.
    /// </summary>
    private async Task<Tokenizer> CreateTokenizerAsync()
    {
        switch (options.Model.TokenizerKind)
        {
            case EmbeddingTokenizer.ByteLevelBpe:
                // Qwen and the GPT-2 lineage. CodeGenTokenizer is the byte-level implementation in
                // Microsoft.ML.Tokenizers; BpeTokenizer is not, and would mangle any non-ASCII input.
                // No [CLS]/[SEP] are added: a causal model has no such tokens.
                using (var vocab = OpenBpeVocab(options.VocabPath))
                await using (var merges = File.OpenRead(options.MergesPath))
                {
                    return CodeGenTokenizer.Create(
                        vocab, merges, addPrefixSpace: false, addBeginOfSentence: false, addEndOfSentence: false);
                }

            case EmbeddingTokenizer.SentencePiece:
                // Gemma wraps every sequence in <bos>…<eos>, and its tokenizer_config asks for both.
                // Letting the tokenizer add them is the point: doing it by hand in Tokenize would put
                // the ids outside the truncation budget and past the graph's position limit.
                await using (var model = File.OpenRead(options.VocabPath))
                {
                    return SentencePieceTokenizer.Create(
                        model, addBeginningOfSentence: true, addEndOfSentence: true);
                }

            default:
                return BertTokenizer.Create(options.VocabPath);
        }
    }

    /// <summary>
    /// Opens a byte-level BPE vocabulary, adding the end-of-text token if the file lacks it.
    ///
    /// <c>CodeGenTokenizer.Create</c> hardcodes <c>&lt;|endoftext|&gt;</c> as its unknown/BOS/EOS
    /// token and throws if the vocabulary does not contain it, while Qwen keeps its specials in
    /// <c>added_tokens</c> rather than in vocab.json, so the two disagree out of the box. Adding the
    /// entry at its real id (151643, matching <c>eos_token_id</c> in the model's config) satisfies
    /// the tokenizer without shifting any existing id, which is the part that would silently corrupt
    /// every embedding.
    /// </summary>
    private static Stream OpenBpeVocab(string path)
    {
        var json = File.ReadAllText(path);
        if (json.Contains("<|endoftext|>", StringComparison.Ordinal))
        {
            return File.OpenRead(path);
        }

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                entry.WriteTo(writer);
            }

            writer.WriteNumber("<|endoftext|>", EosToken);
            writer.WriteEndObject();
        }

        buffer.Position = 0;
        return buffer;
    }

    public float[] Embed(string text) => EmbedBatch([text])[0];

    /// <summary>Embeds a batch in one forward pass; sequences are padded to the batch's longest.</summary>
    public float[][] EmbedBatch(IReadOnlyList<string> texts)
    {
        if (_session is null || _tokenizer is null)
        {
            throw new InvalidOperationException("Embedder not initialized; call EnsureReadyAsync first");
        }

        if (texts.Count == 0)
        {
            return [];
        }

        var rows = texts.Select(Tokenize).ToArray();
        var maxLen = rows.Max(r => r.Length);
        var batch = rows.Length;

        var inputIds = new DenseTensor<long>([batch, maxLen]);
        var mask = new DenseTensor<long>([batch, maxLen]);
        var types = new DenseTensor<long>([batch, maxLen]); // all zero — single-segment
        for (var b = 0; b < batch; b++)
        {
            var row = rows[b];
            for (var t = 0; t < row.Length; t++)
            {
                inputIds[b, t] = row[t];
                mask[b, t] = 1;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };

        if (options.Model.Decoder is { } decoder)
        {
            // A causal export wants position_ids and a zero-length key/value pair per layer. The
            // empty tensors are not optional: ONNX Runtime fails the run if a declared input is
            // absent, and a prefill pass is exactly "no history yet", so length 0 is the correct
            // value rather than a placeholder.
            var positions = new DenseTensor<long>([batch, maxLen]);
            for (var b = 0; b < batch; b++)
            {
                for (var t = 0; t < maxLen; t++)
                {
                    positions[b, t] = t;
                }
            }

            inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", positions));
            for (var layer = 0; layer < decoder.Layers; layer++)
            {
                var empty = new DenseTensor<float>([batch, decoder.KeyValueHeads, 0, decoder.HeadDimension]);
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{layer}.key", empty));
                inputs.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{layer}.value", empty));
            }
        }
        else if (_usesTokenTypeIds)
        {
            // Most encoders take a segment id per token. A decoder has no such input at all, and
            // neither does the Gemma export, which is why this is asked of the graph and not assumed.
            inputs.Add(NamedOnnxValue.CreateFromTensor(TokenTypeIdsInput, types));
        }

        using var results = _session.Run(inputs);
        var pooled = options.Model.Pooling == EmbeddingPooling.Pooled;
        // Both supported precisions emit a float tensor: int8 models are QDQ graphs that dequantize
        // before the output, and fp32 is float by definition. See EmbeddingPrecision for why there
        // is no half-precision case to handle here.
        var output = results.First(r => r.Name == (pooled ? PooledOutput : HiddenStateOutput)).AsTensor<float>();
        var dim = output.Dimensions[^1];
        var mean = options.Model.Pooling == EmbeddingPooling.Mean;

        if (pooled)
        {
            // Already [batch, dim] and already unit length; the normalize below is a no-op kept for
            // the invariant rather than for the arithmetic. Masking happened inside the graph, so
            // padding is handled there and the row lengths are not needed here.
            var done = new float[batch][];
            for (var b = 0; b < batch; b++)
            {
                var vec = new float[dim];
                for (var h = 0; h < dim; h++)
                {
                    vec[h] = output[b, h];
                }

                EmbeddingMath.NormalizeInPlace(vec);
                done[b] = vec;
            }

            return done;
        }

        var vectors = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var vec = new float[dim];
            if (options.Model.Pooling == EmbeddingPooling.LastToken)
            {
                // The final REAL token, not the final padded one. Sequences are right-padded to the
                // batch maximum, so reading maxLen-1 would read padding for every row but the
                // longest - and in a causal model only the last real position has attended to the
                // whole sequence, which is the entire reason this pooling exists.
                var last = rows[b].Length - 1;
                for (var h = 0; h < dim; h++)
                {
                    vec[h] = output[b, last, h];
                }
            }
            else if (mean)
            {
                // Average over the row's real tokens only. rows[b].Length is exactly the unpadded
                // length, so this is the mask-weighted mean without needing to read the mask back:
                // including the padding would drag every short text toward the pad embedding.
                var length = rows[b].Length;
                for (var t = 0; t < length; t++)
                {
                    for (var h = 0; h < dim; h++)
                    {
                        vec[h] += output[b, t, h];
                    }
                }

                for (var h = 0; h < dim; h++)
                {
                    vec[h] /= length;
                }
            }
            else
            {
                for (var h = 0; h < dim; h++)
                {
                    vec[h] = output[b, 0, h]; // CLS token = position 0
                }
            }

            EmbeddingMath.NormalizeInPlace(vec);
            vectors[b] = vec;
        }

        return vectors;
    }

    /// <summary>
    /// Encodes to raw token ids, with the special tokens the model expects.
    ///
    /// The cast is load-bearing and must not be "simplified" back to <c>_tokenizer.EncodeToIds(text)</c>.
    /// <see cref="BertTokenizer"/> HIDES the base <see cref="Tokenizer.EncodeToIds(string, bool, bool)"/>
    /// with an overload of its own whose second parameter is <c>addSpecialTokens</c>, defaulting to
    /// true. Through a <see cref="Tokenizer"/>-typed reference the call binds to the BASE method,
    /// which wraps nothing around the text - so every sequence loses its [CLS] and [SEP], CLS pooling
    /// then reads the first real word instead of [CLS], and the vectors come out plausible-looking
    /// and badly wrong. It fails silently in the worst way: the model loads, embeds, normalizes and
    /// returns unit vectors that simply do not rank. Measured cost when this was live: bge-base fell
    /// from 9/12 to 0/12 on the twelve-query set, with the same text and the same graph.
    ///
    /// The field is typed <see cref="Tokenizer"/> because three unrelated families have to live in
    /// it, so the dispatch has to be explicit here rather than resolved by the compiler.
    /// </summary>
    internal static IReadOnlyList<int> Encode(Tokenizer tokenizer, string text) => tokenizer switch
    {
        BertTokenizer bert => bert.EncodeToIds(text, addSpecialTokens: true),
        _ => tokenizer.EncodeToIds(text),
    };

    private IReadOnlyList<int> Encode(string text) => _tokenizer is { } t
        ? Encode(t, text)
        : throw new InvalidOperationException("Embedder not initialized; call EnsureReadyAsync first");

    /// <summary>
    /// Encodes to token ids, truncated to MaxTokens. The two special-token fixups are WordPiece-only:
    /// 101 and 102 are [CLS]/[SEP] in a WordPiece vocabulary and arbitrary words in Qwen's 151k
    /// byte-level one or Gemma's 262k SentencePiece one, so stamping them elsewhere would embed
    /// noise. A causal model needs no sentinel either side, and SentencePiece adds its own bos/eos
    /// during encoding, so truncation in both cases is a plain cut.
    /// </summary>
    private long[] Tokenize(string text)
    {
        var isDecoder = options.Model.Decoder is not null;
        var isWordPiece = options.Model.TokenizerKind == EmbeddingTokenizer.WordPiece;
        var ids = Encode(text ?? string.Empty);
        var count = Math.Min(ids.Count, options.MaxTokens);

        if (ids.Count == 0)
        {
            // Never zero-length: the graph needs at least one position, and last-token pooling needs
            // a token to read. EOS is the neutral choice for a decoder, [CLS]+[SEP] for BERT, and
            // the model's own bos/eos pair for SentencePiece, whose ids are not conventional.
            return isDecoder ? [EosToken]
                : isWordPiece ? [ClsToken, SepToken]
                : [options.Model.BeginOfSequenceToken, options.Model.EndOfSequenceToken];
        }

        // A decoder embedder's vector is the hidden state at an explicitly appended end-of-text
        // token, not at the last word of the text. Qwen3-Embedding is trained that way, and omitting
        // it does not fail loudly - it reads whatever the final content token happened to be, which
        // measured MRR 0.043 against 0.36 for a model a fifth its size before this was added.
        // The budget is reserved before truncation so the sentinel survives a long passage.
        if (isDecoder)
        {
            count = Math.Min(ids.Count, options.MaxTokens - 1);
        }

        var tokens = new long[isDecoder ? count + 1 : Math.Max(count, 2)];
        for (var i = 0; i < count; i++)
        {
            tokens[i] = ids[i];
        }

        if (isDecoder)
        {
            tokens[count] = EosToken;
        }

        if (isWordPiece && ids.Count > options.MaxTokens)
        {
            tokens[count - 1] = SepToken; // keep a terminating [SEP] after truncation
        }

        return tokens;
    }

    public void Dispose() => _session?.Dispose();
}
