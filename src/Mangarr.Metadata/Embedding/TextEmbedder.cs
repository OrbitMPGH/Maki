using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Mangarr.Metadata.Embedding;

/// <summary>
/// Turns text into a unit-normalized embedding vector using the local bge-small ONNX model.
/// BERT tokenizer → ONNX forward pass → CLS-token pooling (bge convention) → L2 normalize.
/// Runs in-process on CPU; a GPU build of ONNX Runtime would accelerate the batch pass with
/// no code change. Thread-safe once initialized.
/// </summary>
public sealed class TextEmbedder(
    EmbeddingOptions options,
    EmbeddingModelStore modelStore,
    ILogger<TextEmbedder> logger) : IDisposable
{
    private const long ClsToken = 101;
    private const long SepToken = 102;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    public int Dimensions => options.Dimensions;
    public bool IsReady => _session is not null;

    /// <summary>Downloads the model if needed and loads the session/tokenizer. Idempotent.</summary>
    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
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
            _tokenizer = BertTokenizer.Create(options.VocabPath);
            _session = new InferenceSession(options.ModelPath, new SessionOptions());
            logger.LogInformation("Text embedder ready ({Dim}-dim, model {Version})", Dimensions, EmbeddingOptions.ModelVersion);
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
            NamedOnnxValue.CreateFromTensor("token_type_ids", types),
        };

        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();
        var dim = output.Dimensions[^1];

        var vectors = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var vec = new float[dim];
            for (var h = 0; h < dim; h++)
            {
                vec[h] = output[b, 0, h]; // CLS token = position 0
            }

            EmbeddingMath.NormalizeInPlace(vec);
            vectors[b] = vec;
        }

        return vectors;
    }

    /// <summary>Encodes to token ids, truncated to MaxTokens with the closing [SEP] preserved.</summary>
    private long[] Tokenize(string text)
    {
        var ids = _tokenizer!.EncodeToIds(text ?? string.Empty);
        var count = Math.Min(ids.Count, options.MaxTokens);
        var tokens = new long[Math.Max(count, 2)];
        if (ids.Count == 0)
        {
            tokens[0] = ClsToken;
            tokens[1] = SepToken;
            return tokens;
        }

        for (var i = 0; i < count; i++)
        {
            tokens[i] = ids[i];
        }

        if (ids.Count > options.MaxTokens)
        {
            tokens[count - 1] = SepToken; // keep a terminating [SEP] after truncation
        }

        return tokens;
    }

    public void Dispose() => _session?.Dispose();
}
