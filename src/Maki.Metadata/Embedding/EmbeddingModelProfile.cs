namespace Maki.Metadata.Embedding;

/// <summary>
/// How a model turns per-token hidden states into one vector. Not interchangeable: pooling a
/// mean-pooled model at the CLS position (or the reverse) reads a position the training never gave
/// meaning to, and produces plausible-looking vectors that rank badly.
/// </summary>
public enum EmbeddingPooling
{
    /// <summary>Position 0. What every bge model and both Snowflake/mixedbread encoders use.</summary>
    Cls,

    /// <summary>Mask-weighted average over real tokens. What the e5 and gte families use.</summary>
    Mean,

    /// <summary>
    /// The final real token. What decoder-based embedders (Qwen3-Embedding) use, because a causal
    /// model can only have attended to the whole sequence at the last position. Implies a decoder
    /// graph, which needs <see cref="EmbeddingModelProfile.Decoder"/> set.
    /// </summary>
    LastToken,

    /// <summary>
    /// The graph pools itself and exposes the finished vector as a second output
    /// (<c>sentence_embedding</c>). Used by exports that baked the whole sentence-transformers module
    /// stack in, EmbeddingGemma being the case here: after mean pooling it runs two Dense layers
    /// (768 to 3072 to 768) and an L2 normalize, so pooling <c>last_hidden_state</c> by hand would
    /// skip both projections and yield vectors that look fine and rank badly.
    /// </summary>
    Pooled,
}

/// <summary>
/// Which tokenizer implementation a model's vocabulary needs. Not inferable from the files: a
/// vocab.json belongs to a byte-level BPE model and a tokenizer.model to a SentencePiece one, but
/// both arrive as "the vocab" and picking the wrong one produces ids that are silently meaningless
/// rather than an error.
/// </summary>
public enum EmbeddingTokenizer
{
    /// <summary>WordPiece over a plain vocab.txt. Every BERT-lineage model: bge, e5, gte, arctic.</summary>
    WordPiece,

    /// <summary>vocab.json + merges.txt, the GPT-2 lineage. Qwen3-Embedding.</summary>
    ByteLevelBpe,

    /// <summary>A SentencePiece tokenizer.model protobuf. Gemma, Llama and the T5 lineage.</summary>
    SentencePiece,
}

/// <summary>
/// Shape of a decoder embedder's ONNX graph. Encoder exports (bge, e5, gte) need none of this: they
/// take three tensors and return one. A causal export takes <c>position_ids</c> as well, and a
/// <c>past_key_values.N.key</c>/<c>.value</c> pair per layer that must be fed as zero-length tensors
/// on a prefill-only pass, plus it returns a <c>present.N.*</c> pair per layer that is discarded.
/// For Qwen3-Embedding-0.6B that is 56 extra inputs and 56 extra outputs, and ONNX Runtime rejects
/// the run outright if any input is missing, so the geometry has to be declared rather than guessed.
/// </summary>
public sealed record DecoderGraph(int Layers, int KeyValueHeads, int HeadDimension);

/// <summary>
/// A selectable embedding model. Maki used to ship a heavier "large" option alongside the default;
/// it was retired (see below), so <see cref="Resolve"/> now only ever returns <see cref="Base"/>.
///
/// Measured on the full catalogue with distribution/run-eval.ps1 (2,000 held-out descriptions,
/// title words stripped, MRR@10): base (arctic-m) 0.4561, large (bge-large) 0.3392. The heavier
/// option was never the better one — bge-large was statistically indistinguishable from the
/// bge-base it used to be paired with (p=0.20) and clearly behind arctic-m, while costing ~+260 MB
/// resident and ~+230 MB download. Accounts still holding the "large" setting are migrated to
/// "base" automatically (see the Program.cs startup read of
/// <see cref="Maki.Core.Configuration.SettingKeys.RecommendationsEmbeddingModel"/>).
///
/// <see cref="Version"/> is part of every stored vector's content hash, so bumping it (a new model,
/// or a change to the embedded-text formula) invalidates the index and forces a one-time re-embed.
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
    /// <summary>How to collapse the token states into a vector. See <see cref="EmbeddingPooling"/>.</summary>
    public EmbeddingPooling Pooling { get; init; } = EmbeddingPooling.Cls;

    /// <summary>Which tokenizer the vocabulary needs. See <see cref="EmbeddingTokenizer"/>.</summary>
    public EmbeddingTokenizer TokenizerKind { get; init; } = EmbeddingTokenizer.WordPiece;

    /// <summary>
    /// Special-token ids for <see cref="EmbeddingTokenizer.SentencePiece"/>, whose values are per
    /// model rather than conventional (Gemma: bos 2, eos 1, against Llama's 1 and 2). Only used to
    /// build the stand-in for empty input; the tokenizer adds them to real text itself.
    /// </summary>
    public long BeginOfSequenceToken { get; init; } = 2;

    public long EndOfSequenceToken { get; init; } = 1;

    /// <summary>
    /// Set for a causal export; null for every encoder model, which is all Maki ships. See
    /// <see cref="DecoderGraph"/>.
    /// </summary>
    public DecoderGraph? Decoder { get; init; }

    /// <summary>
    /// Byte-level BPE vocabulary and merges, for models whose tokenizer is not WordPiece. When set,
    /// <c>VocabUrl</c> is the vocab.json and this is the merges.txt; <see cref="TextEmbedder"/> then
    /// builds a CodeGen-style tokenizer instead of a BERT one. Null keeps the WordPiece path.
    /// </summary>
    public string? MergesUrl { get; init; }

    /// <summary>
    /// Companion weights file for an ONNX graph stored in external-data form. Models above the 2 GB
    /// protobuf limit split into a small .onnx plus a large .onnx_data that ONNX Runtime resolves by
    /// name next to it, so both have to be downloaded and both have to land in the same folder.
    /// </summary>
    public string? ModelDataUrl { get; init; }

    /// <summary>
    /// Prepended to a search query before embedding. bge is asymmetric and trained with this exact
    /// sentence, and dropping it costs recall; the e5 family wants "query: " instead, and gte was
    /// trained symmetrically and wants nothing. Getting it wrong is a silent quality loss, not an
    /// error, which is why it belongs to the model rather than to the search code.
    /// </summary>
    public string QueryPrefix { get; init; } = "Represent this sentence for searching relevant passages: ";

    /// <summary>
    /// Prepended to an indexed passage. Empty for bge (passages are indexed bare, which is the other
    /// half of its asymmetry) and for gte; "passage: " for e5, where omitting it mismatches the
    /// query side and quietly degrades every result.
    /// </summary>
    public string PassagePrefix { get; init; } = "";

    /// <summary>
    /// snowflake-arctic-embed-m, 768-dim. The default: ~240 MB resident, ~110 MB model download.
    ///
    /// Replaced bge-base-en-v1.5 at q5, at identical cost - same 768 dims, same ~110M parameters,
    /// same CLS pooling, and the same query instruction, so nothing but the weights changed. Measured
    /// on the full 95,745-series catalogue (distribution/run-eval.ps1), 2,000 held-out descriptions
    /// with title words stripped: MRR@10 0.4561 against bge-base's 0.3512, paired difference +0.1049
    /// with a bootstrap 95% interval of [+0.0895, +0.1202], and recall@1 taken on 290 queries where
    /// bge-base fails against 83 the other way (McNemar exact p=5.3e-28).
    ///
    /// Two honest caveats, both recorded so nobody re-derives them:
    /// * On the 151 hand-written queries (distribution/eval-queries.tsv), which look far more like
    ///   what somebody types, arctic-m only TIES bge-base on the `premise` class (+0.0565, interval
    ///   spans zero). It wins the big-sample eval and never loses; that is the whole case.
    /// * EmbeddingGemma-300m beats it on the hand-written set overall (+0.083, p=0.034), mostly on
    ///   title and alias queries that the FTS5 channel already answers, and costs ~309 MB. That is
    ///   why it is not the default.
    /// </summary>
    public static readonly EmbeddingModelProfile Base = new(
        Kind: "base",
        FolderName: "snowflake-arctic-embed-m",
        Dimensions: 768,
        // q4: dropped the genre/theme facet block from the embedded text and preferred the
        // MangaUpdates description where present (see SeriesEmbeddingIndexer.BuildText). Measured
        // 0.393 → 0.545 MRR on the 12-query set.
        // q5: bge-base-en-v1.5 → snowflake-arctic-embed-m (see the summary above). The version string
        // is what stops a published bge-base index being installed into an arctic-m install:
        // PrebuiltIndexInstaller compares it exactly, and the DIMENSION CHECK CANNOT CATCH THIS ONE
        // because both models are 768-dim. Forces the one-time re-embed.
        Version: "snowflake-arctic-embed-m-q5",
        ModelUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/onnx/model_quantized.onnx",
        VocabUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/vocab.txt",
        // Deliberately the same tag. The version check above makes a stale index on either side safe:
        // an old client rejects a newly published arctic-m index, a new client rejects the old
        // bge-base one, and both fall back to embedding locally rather than loading wrong vectors.
        PrebuiltTag: "embeddings-base-latest");

    /// <summary>
    /// This model's ONNX export at a given precision. <see cref="ModelUrl"/> is the int8 one, and
    /// every model we ship is a Xenova export whose <c>onnx/</c> folder holds <c>model.onnx</c>,
    /// <c>model_fp16.onnx</c> and <c>model_quantized.onnx</c> side by side, so the precision is the
    /// final path segment and nothing else about the URL changes.
    /// </summary>
    public string ModelUrlFor(EmbeddingPrecision precision) =>
        precision == EmbeddingPrecision.Int8
            ? ModelUrl
            : ModelUrl[..(ModelUrl.LastIndexOf('/') + 1)] + EmbeddingRuntime.UpstreamFileName(precision);

    /// <summary>
    /// The weights half of an external-data graph, at the same precision. Derived rather than stored,
    /// because the name is not free on either end: the exporter writes "&lt;graph file&gt;.onnx_data"
    /// and the graph records that name internally, so a hardcoded URL would fetch the int8 weights
    /// for an fp32 graph and the session would fail to load with nothing useful to say about why.
    /// Null for the ordinary single-file models, which is all Maki ships.
    /// </summary>
    public string? ModelDataUrlFor(EmbeddingPrecision precision) =>
        ModelDataUrl is null ? null : ModelUrlFor(precision) + "_data";

    /// <summary>The "model" value that means embeddings are turned off entirely.</summary>
    public const string OffKind = "off";

    /// <summary>True when the user has turned embeddings off (search/recs fall back to lexical/genre).</summary>
    public static bool IsOff(string? kind) => string.Equals(kind, OffKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The configured model. "large" was retired as a selectable tier (see the note above), so this
    /// always resolves to <see cref="Base"/>; the <paramref name="kind"/> parameter is kept only so
    /// callers don't need special-casing. "off" is not a model — it resolves to Base here as a
    /// harmless placeholder, and <see cref="IsOff"/> gates whether the embedding paths run at all
    /// (see <see cref="EmbeddingOptions.Enabled"/>).
    /// </summary>
    public static EmbeddingModelProfile Resolve(string? kind) => Base;
}
