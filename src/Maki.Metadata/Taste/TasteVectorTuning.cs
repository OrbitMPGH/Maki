namespace Maki.Metadata.Taste;

/// <summary>
/// Dials for the behavioural channel. Registered as a singleton and never mutated at runtime; the
/// eval overrides it per variant, which is the only way any of these numbers get chosen.
/// </summary>
public sealed record TasteVectorTuning
{
    public static readonly TasteVectorTuning Default = new();

    /// <summary>
    /// Coefficient on the behavioural cosine in <c>EmbeddingMath.HybridScore</c>. Applied only when
    /// an artifact is actually loaded, so a missing file leaves results byte-identical to before the
    /// channel existed.
    ///
    /// <para>
    /// Read against <c>Weights.Semantic = 3.0</c>: this is a cosine in the same [-1, 1] range and
    /// answers the same question from the other side, so it is the one channel here whose weight is
    /// directly comparable to the text channel's.
    /// </para>
    /// </summary>
    public double Weight { get; init; } = 1.5;

    /// <summary>
    /// How many query vectors the behavioural channel builds from the seed set, on top of the
    /// centroid. Kept separate from <c>RecommenderTuning.MaxSeedQueries</c> because the two spaces
    /// have different dimensionality and therefore different per-query cost.
    /// </summary>
    public int MaxSeedQueries { get; init; } = 8;

    /// <summary>
    /// Cosine a row must reach, as a fraction of the best behavioural candidate's, before the
    /// channel may inject it into a pool the text channels never selected.
    ///
    /// <para>
    /// Injection is the coverage win and the risk in one: the artifact reaches 94,686 rows where the
    /// co-read graph reaches 41,054, so it can surface candidates no text query would. But pool
    /// entry is a bigger privilege than it looks, because <c>HybridScore</c> ranks on genre, tag,
    /// author and quality too, so an injected row can top the final ranking without being anywhere
    /// near the cosine top-200. Same gate, and the same reasoning, as
    /// <c>RecoGraphTuning.MinInjectedScore</c>.
    /// </para>
    /// </summary>
    public double MinInjectedScore { get; init; } = 0.70;

    /// <summary>
    /// Hard cap on injected rows per request. A cap that is inert today was load-bearing on a
    /// smaller artifact and may be again, so it stays sweepable rather than being removed the moment
    /// the threshold turns out to bind first.
    /// </summary>
    public int MaxInjected { get; init; } = 100;
}
