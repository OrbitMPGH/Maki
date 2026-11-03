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

    /// <summary>
    /// Whether the embedded passage carries a facet clause (demographic, format, original house,
    /// heaviest story tags) between the title and the description. See
    /// <c>SeriesEmbeddingIndexer.BuildFacets</c>.
    ///
    /// <para>
    /// A property of the PROFILE rather than a setting, because it changes what a vector means: a
    /// faceted passage and a plain one are not comparable, so the two cannot share an index, a
    /// published artifact, or a <see cref="Version"/>. Anything turning this on must move the
    /// version string with it or a client will install vectors built from a different text and
    /// report nothing wrong.
    /// </para>
    /// </summary>
    public bool PassageFacets { get; init; }

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
    /// For <see cref="EmbeddingTokenizer.SentencePiece"/>: the graph expects XLM-R's fairseq id
    /// layout rather than the SentencePiece model's own. Fairseq puts &lt;s&gt; &lt;pad&gt; &lt;/s&gt;
    /// &lt;unk&gt; at 0 to 3 and shifts every piece up by one, so the raw ids are all off by one and
    /// still look valid. XLM-R models ship only a tokenizer.json, so the vocab is xlm-roberta-base's
    /// sentencepiece.bpe.model, which this remaps. Also keeps &lt;/s&gt; on a truncated sequence.
    /// </summary>
    public bool FairseqTokenIds { get; init; }

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
    /// snowflake-arctic-embed-m-v1.5, stored at its first 256 of 768 dimensions. The default: the
    /// same ~110 MB download and the same resident size as v1 (+139 MB private while loaded, both
    /// measured on CPU at int8), and a vector index a third the size: 128 bytes a row packed against
    /// v1's 384, 16 MB at catalogue scale against 49 MB.
    ///
    /// <para>
    /// v1.5 is trained with Matryoshka loss, so its leading 256 dimensions stand on their own, and
    /// <see cref="TextEmbedder"/> keeps those and renormalizes. v1 is not: cut to 256 it falls from
    /// MRR@10 0.4382 to 0.2679 on 2,000 held-out descriptions. Measured against v1 at 768 on the
    /// 128,403-row catalogue (distribution/CLAUDE.md, "v6: the embedding model, at 256 dimensions"):
    /// the full search fusion over the 190 hand-written queries +0.0255 MRR, 95% [+0.0002, +0.0527],
    /// and recommendation nDCG@40 indistinguishable on every grader that is not trained on the same
    /// readers (single seed against MangaUpdates +0.0009 [-0.0025, +0.0045], held-out reading lists
    /// -0.0004 [-0.0056, +0.0047]). The dense channel alone gives up 0.06 MRR on held-out
    /// descriptions; the other channels absorb it.
    /// </para>
    ///
    /// <para>
    /// Snowflake's export pools (CLS) and normalizes inside the graph and takes no token_type_ids,
    /// unlike v1's, hence <see cref="EmbeddingPooling.Pooled"/>. arctic-embed-m-v2.0 was measured
    /// too and is the multilingual one, but it is a 305M-parameter model (+251 MB resident) that
    /// loses English premise search to this one by 0.108 MRR, and its cross-lingual retrieval is
    /// weak (0.2 MRR in German, French and Spanish, under 0.12 in Japanese, Korean and Chinese).
    /// </para>
    /// </summary>
    public static readonly EmbeddingModelProfile Base = new(
        Kind: "base",
        FolderName: "snowflake-arctic-embed-m-v1.5",
        Dimensions: 256,
        // q4: dropped the genre/theme facet block from the embedded text and preferred the
        // MangaUpdates description where present (see SeriesEmbeddingIndexer.BuildText).
        // q5: bge-base-en-v1.5 to snowflake-arctic-embed-m.
        // q6: arctic-embed-m to arctic-embed-m-v1.5 at 256 dimensions. The version string is what
        // stops a v1 index being installed into a v1.5 install; PrebuiltIndexInstaller compares it
        // exactly. With no prebuilt index published for it, an upgraded install has no usable
        // vectors until an admin runs the local pass.
        Version: "snowflake-arctic-embed-m-v1.5-256-q6",
        ModelUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/onnx/model_quantized.onnx",
        VocabUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/vocab.txt",
        // A new tag rather than the old one: installs still on v1 keep reading embeddings-base-latest,
        // and this one can be published before the release that reads it.
        PrebuiltTag: "embeddings-base-v15-256-latest")
    {
        Pooling = EmbeddingPooling.Pooled,
    };

    /// <summary>
    /// snowflake-arctic-embed-m at 768, the default through q5, kept so the distribution tools can
    /// still grade an index built with it. Its version string is the one those indexes carry.
    /// </summary>
    public static readonly EmbeddingModelProfile ArcticV1 = new(
        Kind: "arctic-m",
        FolderName: "snowflake-arctic-embed-m",
        Dimensions: 768,
        Version: "snowflake-arctic-embed-m-q5",
        ModelUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/onnx/model_quantized.onnx",
        VocabUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/vocab.txt",
        PrebuiltTag: "embeddings-base-latest");

    /// <summary>
    /// Model folders earlier releases downloaded into the models directory, removed once the
    /// current model is in place (<see cref="EmbeddingModelStore.RemoveRetiredModels"/>). Listed by
    /// name rather than swept, because the same directory holds the upscaler's models.
    /// </summary>
    public static IReadOnlyList<string> RetiredFolderNames { get; } =
        ["bge-small-en-v1.5", "bge-base-en-v1.5", "bge-large-en-v1.5", "snowflake-arctic-embed-m"];

    /// <summary>
    /// Profiles measured against <see cref="Base"/> by the distribution tools (build-embeddings,
    /// eval-search, eval-reco-labels take one by key), so those three cannot disagree about a model.
    /// eval-embeddings keeps its own table, since it also scores models the app never loads.
    /// A <c>-256</c> entry stores the model's first 256 dimensions; only Matryoshka-trained models
    /// (both v1.5 and v2.0 are, v1 is not) keep their quality there. <c>arctic-m15-256</c> is
    /// <see cref="Base"/> under an evaluation version string. Nothing in the app reads these.
    /// </summary>
    public static IReadOnlyDictionary<string, EmbeddingModelProfile> Candidates { get; } = BuildCandidates();

    private static Dictionary<string, EmbeddingModelProfile> BuildCandidates()
    {
        var v15 = new EmbeddingModelProfile(
            Kind: "arctic-m15",
            FolderName: "snowflake-arctic-embed-m-v1.5",
            Dimensions: 768,
            Version: "snowflake-arctic-embed-m-v1.5-eval",
            ModelUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v1.5/resolve/main/vocab.txt",
            PrebuiltTag: "unpublished")
        {
            // Snowflake's export pools (CLS) and normalizes inside the graph, unlike v1's.
            Pooling = EmbeddingPooling.Pooled,
        };

        // A GTE-multilingual encoder despite the name: 305M parameters, a 250k XLM-R vocabulary,
        // pooled and normalized inside the graph. The repo ships only tokenizer.json, so the vocab
        // is xlm-roberta's SentencePiece model with the fairseq id shift (FairseqTokenIds).
        var v2 = new EmbeddingModelProfile(
            Kind: "arctic-m2",
            FolderName: "snowflake-arctic-embed-m-v2.0",
            Dimensions: 768,
            Version: "snowflake-arctic-embed-m-v2.0-eval",
            ModelUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v2.0/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/FacebookAI/xlm-roberta-base/resolve/main/sentencepiece.bpe.model",
            PrebuiltTag: "unpublished")
        {
            Pooling = EmbeddingPooling.Pooled,
            TokenizerKind = EmbeddingTokenizer.SentencePiece,
            FairseqTokenIds = true,
            BeginOfSequenceToken = 0,
            EndOfSequenceToken = 2,
            QueryPrefix = "query: ",
            PassagePrefix = "",
        };

        static EmbeddingModelProfile Narrow(EmbeddingModelProfile profile, int dimensions) => profile with
        {
            Kind = $"{profile.Kind}-{dimensions}",
            Dimensions = dimensions,
            Version = $"{profile.Version}-{dimensions}",
        };

        var all = new[] { ArcticV1, v15, Narrow(v15, 256), v2, Narrow(v2, 256) };
        return all.ToDictionary(p => p.Kind, StringComparer.OrdinalIgnoreCase);
    }

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
