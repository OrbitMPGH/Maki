namespace Maki.Metadata.Embedding;

/// <summary>
/// How <see cref="SemanticRecommender"/> chooses which individual seeds get their own query, once a
/// library has more of them than <see cref="RecommenderTuning.MaxSeedQueries"/>.
/// </summary>
public enum SeedSelection
{
    /// <summary>
    /// Greedy farthest-point sampling from the highest-weighted seed: each next pick is the seed
    /// least similar to everything picked so far. Spreads the queries across the whole library
    /// rather than spending them on eight volumes of one series, which is what it was written for
    /// — but by construction it walks the OUTSIDE of the seed set, so the picks after the first are
    /// the library's corners rather than its centres of mass, and weight only chooses where the
    /// walk starts.
    /// </summary>
    Farthest,

    /// <summary>
    /// The highest-weighted seeds, nothing else. The naive option, kept because "the thing the
    /// reader liked most" is the obvious hypothesis and it deserves a number rather than an
    /// argument. Expected to spend queries on near-duplicates.
    /// </summary>
    Weight,

    /// <summary>
    /// Weighted k-means over the seed vectors, then the seed nearest each cluster's mean. Picks
    /// centres rather than corners: one query per thing the library is actually about, sized by how
    /// much of the library sits there.
    /// </summary>
    Medoid,

    /// <summary>
    /// Farthest-point sampling with the weight folded into every step rather than only the first,
    /// so the walk prefers seeds that are both unlike what is already picked and actually liked.
    /// </summary>
    WeightedFarthest,
}

/// <summary>
/// The two knobs on <see cref="SemanticRecommender"/> that are not a channel coefficient. Broken out
/// as a record for the same reason <see cref="SearchTuning"/> and
/// <see cref="RecoGraph.RecoGraphTuning"/> are: so <c>distribution/eval-reco-labels.cs</c> can sweep
/// them, and so the defaults sit next to the measurement behind them.
///
/// <para>
/// Registered as a singleton holding <see cref="Default"/>; nothing in the app changes it at runtime.
/// </para>
/// </summary>
public sealed record RecommenderTuning
{
    public static readonly RecommenderTuning Default = new();

    /// <summary>
    /// Below this seed-to-candidate cosine, "feel" is too weak to recommend on and the candidate is
    /// dropped however well it scores on the structured channels.
    /// </summary>
    public double CosineFloor { get; init; } = 0.30;

    /// <summary>
    /// Whether a candidate a crowd graph vouched for may enter the ranking under
    /// <see cref="CosineFloor"/>.
    ///
    /// <para>
    /// The floor runs after injection, so today a row the crowd channels put into the pool is
    /// dropped moments later if the embeddings did not already find it plausible — which caps the
    /// channel whose entire purpose is surfacing what the embeddings rank 40,000th, and spends
    /// <c>MaxInjected</c> on rows that cannot survive. That is either the floor doing its job or the
    /// discovery channel being quietly disabled, and the two are indistinguishable without measuring.
    /// </para>
    ///
    /// <para>
    /// Defaults to false, which is the behaviour that shipped. Flip it in the eval before flipping it
    /// here.
    /// </para>
    /// </summary>
    public bool CrowdBypassesCosineFloor { get; init; }

    /// <summary>
    /// Scores the genre channel the way it was scored before
    /// <see cref="SemanticRecommender.GenreScore"/> made it a cosine: a raw sum of the matched
    /// profile weights, whose scale moved with how concentrated the seed set was.
    ///
    /// <para>
    /// Nothing in the app sets this and nothing should. It exists so the eval can reproduce the old
    /// behaviour on demand and keep the before/after reproducible after the code has moved on — the
    /// same reason <see cref="SearchTuning.TagFloorAbsolute"/> survives as a knob whose documented
    /// purpose the data contradicts.
    /// </para>
    /// </summary>
    public bool GenreChannelIsRawSum { get; init; }

    /// <summary>
    /// How many individual seeds get their own query, on top of the centroid. Each one costs another
    /// dot product per catalogue row, so this is the knob that decides what a large library costs.
    ///
    /// <para>
    /// It was 8, and 8 was too few. Measured over real reading lists with 20% held out
    /// (<c>eval-reco-labels.cs library</c>), relevance climbs with every query added and the cost
    /// climbs linearly beside it: nDCG@40 0.096 / 0.104 / 0.112 / 0.116 / 0.117 / 0.121 at 4, 8, 16,
    /// 24, 32 and 48, with per-request latency 130 ms at 8, 225 at 16, 303 at 24 and 534 at 48.
    /// Median pick popularity barely moves, so it is not buying the gain by returning famous titles.
    /// Against 8 the paired difference is +0.0069 nDCG at 16, bootstrap 95% [+0.0035, +0.0104], and
    /// +0.0095 at 24.
    /// </para>
    ///
    /// <para>
    /// 16 rather than 48 because the curve is logarithmic and the price is not, and because the
    /// whole pool is cached for twelve hours per user - the ~95 ms is paid once, but it is paid on a
    /// page load somebody is waiting for. Anything past 24 is a deliberate trade, not a free win.
    /// </para>
    /// </summary>
    public int MaxSeedQueries { get; init; } = 16;

    /// <summary>
    /// Which seeds those are. See <see cref="SeedSelection"/>; only has any effect on a library
    /// holding more than <see cref="MaxSeedQueries"/> seeds, since below that every seed is queried
    /// and the strategy cannot matter — so it never touches the "More like this" rail or a small
    /// seeded Discover.
    ///
    /// <para>
    /// Stays <see cref="SeedSelection.Farthest"/>, and the alternatives measured
    /// <em>indistinguishable</em>: over 800 real libraries, medoid selection beat it by +0.005 MRR
    /// with a bootstrap interval of [-0.007, +0.018]. Farthest-point sampling genuinely does return
    /// the seed set's hull rather than its centres of mass — <c>SeedSelectionTests</c> pins exactly
    /// that — but it does not matter, because the centroid query already covers the mass and the
    /// crowd channels cover what neither reaches. The knob that mattered was how many, not which.
    /// </para>
    /// </summary>
    public SeedSelection SeedSelection { get; init; } = SeedSelection.Farthest;
}
