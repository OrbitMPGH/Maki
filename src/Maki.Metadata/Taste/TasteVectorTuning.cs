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
    ///
    /// <para>
    /// Shipped at 1.5, raised to 2.5 by the only thing two independent fits agreed about. On
    /// held-out READERS that is <b>+0.0035 nDCG@40</b>, bootstrap 95% [+0.0005, +0.0065], and on the
    /// independent pair grader it is indistinguishable (-0.0012, 95% [-0.0041, +0.0014]) while
    /// demographic agreement rises 69% to 71% and the era gap narrows 0.67 to 0.61 decades.
    /// </para>
    ///
    /// <para>
    /// 4.0 has a larger point estimate (+0.0052) and an interval that spans zero, so it is not
    /// shipped: the same reading that rejects a wider knob elsewhere in this codebase. What makes
    /// 2.5 safe rather than merely better is that median pick popularity does not move with it -
    /// this channel buys agreement without buying fame, which is exactly what the fitted vectors
    /// that wanted 4.6 and 13.2 could not claim.
    /// </para>
    /// </summary>
    public double Weight { get; init; } = 2.5;

    /// <summary>
    /// How many query vectors the behavioural channel builds from the seed set, on top of the
    /// centroid. Zero, which ships: the centroid alone is the best retrieval this space has.
    ///
    /// <para>
    /// THE TWO VECTOR SPACES WANT OPPOSITE RETRIEVAL STRATEGIES, and this is the measurement that
    /// says so. In the text space the centroid dilutes - a library that is two unrelated things has
    /// a centroid near neither, which is why <c>RecommenderTuning.MaxSeedQueries</c> is 48 and why
    /// raising it kept paying. Here it is monotone the other way: on held-out readers nDCG@40 is
    /// 0.182 / 0.179 / 0.175 / 0.170 / 0.172 at 0 / 2 / 4 / 8 / 16 seed queries, and 0 against the
    /// shipped 8 is <b>+0.0121</b>, bootstrap 95% [+0.0056, +0.0187], with hit rate 91% to 95%.
    /// </para>
    ///
    /// <para>
    /// It is not a surprise once stated. These vectors are factorized out of whole reading lists, so
    /// a reader's centroid in this space is already the thing the factorization was fitted to
    /// predict; querying individual seeds asks it to be a narrower reader than it is, and the answers
    /// come back correspondingly narrower. The text space has no equivalent, because nothing fitted
    /// a description embedding to represent a person.
    /// </para>
    ///
    /// <para>
    /// Indistinguishable on the independent pair grader (-0.0007, 95% [-0.0062, +0.0050]) and a
    /// no-op at one seed, where there is no seed query to build. Cheaper too, which is the rare part:
    /// the knob that measures best is also the one that does least work. Eval knob:
    /// <c>tasteseedqueries</c>.
    /// </para>
    /// </summary>
    public int MaxSeedQueries { get; init; }

    /// <summary>
    /// Cosine a row must reach, as a fraction of the best behavioural candidate's, before the
    /// channel may inject it into a pool the text channels never selected.
    ///
    /// <para>
    /// Injection is the coverage win and the risk in one: the artifact reaches 60,053 rows where the
    /// co-read graph reaches 41,054, so it can surface candidates no text query would. But pool
    /// entry is a bigger privilege than it looks, because <c>HybridScore</c> ranks on genre, tag,
    /// author and quality too, so an injected row can top the final ranking without being anywhere
    /// near the cosine top-200. Same gate, and the same reasoning, as
    /// <c>RecoGraphTuning.MinInjectedScore</c>.
    /// </para>
    /// </summary>
    /// <para>
    /// Swept on held-out readers and inert across the band: 0.50, 0.70 and 0.85 return nDCG@40
    /// 0.169 / 0.170 / 0.167 with median pick popularity unmoved. That matches both crowd channels,
    /// whose injection gates went inert once their artifacts got dense enough - fewer candidates
    /// clear the floor than any cap or threshold would ever admit. Kept sweepable for the same
    /// reason theirs are: a gate inert on this artifact was load-bearing on a smaller one.
    /// </para>
    public double MinInjectedScore { get; init; } = 0.70;

    /// <summary>
    /// Hard cap on injected rows per request.
    ///
    /// <para>
    /// <b>The one injection cap in the recommender that is not inert, and 100 sits on its knee.</b>
    /// Both crowd graphs hit their <c>MinInjectedScore</c> long before their caps, so theirs are
    /// documented as headroom; this artifact is dense enough that the cap binds first. Swept on the
    /// independent grader at one seed, nDCG@40 is 0.134 at a cap of 10 and 0.140 from 100 upward,
    /// with mean reciprocal rank 0.134 against 0.148 and demographic agreement 63% against 68%.
    /// Held-out readers agree on the same shape: 0.198 at 10, 0.207 at 50, 0.208 at 100.
    /// </para>
    ///
    /// <para>
    /// Above 100 the threshold takes over and the value stops mattering: 200, 300, 600 and 1200
    /// return the same nDCG on 800 requests, and the paired difference against 100 is negative
    /// (-0.0033, 95% [-0.0072, +0.0007]) with recall up and reciprocal rank down. So raising it is a
    /// trade rather than a gain, and lowering it costs outright. Kept sweepable, like the other two.
    /// </para>
    /// </summary>
    public int MaxInjected { get; init; } = 100;
}
