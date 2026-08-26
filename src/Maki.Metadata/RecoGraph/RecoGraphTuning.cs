namespace Maki.Metadata.RecoGraph;

/// <summary>
/// The knobs behind the co-recommendation channel. Broken out as a record for the same reason
/// <see cref="Embedding.SearchTuning"/> is, so <c>distribution/eval-reco.cs</c> can sweep them
/// without a rebuild per value, and so the defaults sit next to the measurement that justifies
/// them.
///
/// <para>
/// Registered as a singleton holding <see cref="Default"/>; nothing in the app changes it at
/// runtime. It lives here rather than beside <c>TasteTuning</c> in Maki.Core because every consumer
/// is in this project — putting it in the domain would leave a tuning record the domain never
/// reads. Same placement, and same reasoning, as <see cref="Embedding.SearchTuning"/>.
/// </para>
///
/// <para>
/// The defaults were swept with <c>eval-reco.cs spread</c> over its 24 synthetic profiles, which is
/// the no-labels over-fit measurement rather than a relevance one — see <see cref="MaxInjected"/>
/// for the table. What it establishes is that the channel does <em>not</em> collapse into a
/// popularity chart at any setting tested; what it cannot establish is that these picks are better,
/// because no labelled corpus of reading behaviour exists to say so. Treat <see cref="Weight"/> in
/// particular as unproven: it barely moved anything in the sweep, which means it is under-measured,
/// not that it is well chosen.
/// </para>
/// </summary>
public sealed record RecoGraphTuning
{
    public static readonly RecoGraphTuning Default = new();

    /// <summary>
    /// The channel's coefficient in <see cref="Embedding.EmbeddingMath.HybridScore"/>, against a
    /// graph score normalized to [0,1]. Sits between Author (0.75) and Quality (0.5) so a strong
    /// co-recommendation is worth about as much as sharing an author — real evidence, still an
    /// order below the semantic cosine at 3.0, which is what keeps this a bonus rather than a
    /// second ranking.
    /// </summary>
    public double Weight { get; init; } = 0.6;

    /// <summary>
    /// Exponent on the neighbour's degree, dividing its contribution. Fixes a failure that is
    /// obvious once measured: at 0 the channel is a shonen popularity chart, because a handful of
    /// mega-titles are paired with everything and their vote counts dwarf everything else.
    ///
    /// <para>
    /// Measured on a real 14-series library whose taste splits between shonen and romance/BL:
    /// at 0 the entire top 15 is One Piece, Bleach, Jujutsu Kaisen and friends, and the romance
    /// half contributes nothing; at 0.5 (full cosine-style normalization on both ends) it inverts
    /// into degree-1 obscurities that a single person paired with a single other title. 0.25 is the
    /// transition between those, which is a reason to start there and not a reason to believe it.
    /// </para>
    /// </summary>
    public double DegreePenalty { get; init; } = 0.25;

    /// <summary>
    /// Added to a neighbour's degree before the penalty is applied, so a barely-connected title does
    /// not get a free pass.
    ///
    /// <para>
    /// Without it, <c>degree^DegreePenalty</c> at degree 1 is exactly 1.0 — no penalty whatsoever —
    /// and a series with a single 13-vote edge outranks a well-connected one with 200 votes across
    /// 30 edges. That is not hypothetical: it put an obscure one-shot at rank 1 of a real library's
    /// recommendations, reached through exactly one seed, which is the thinnest evidence the graph
    /// can express.
    /// </para>
    ///
    /// <para>
    /// Read it as a prior that every series has a good many more neighbours than this artifact has
    /// observed — which is literally true while the catalogue is only partly fetched, and stays
    /// roughly true afterwards, since a title's pair count is bounded by how many people bothered
    /// to submit a recommendation rather than by how related it actually is.
    /// </para>
    ///
    /// <para>
    /// 20 rather than something smaller because 5 was measured and was not enough: the one-shot
    /// above fell from rank 6 to rank 60 within the channel and still came back at rank 1 of the
    /// finished recommendations. At 20 it falls to rank 122 and its normalized score to 0.44.
    /// <c>eval-reco.cs spread</c> reports the same diversity numbers at 5, 20 and 40, so nothing
    /// measurable is given up — and that flatness is the point of the next paragraph.
    /// </para>
    ///
    /// <para>
    /// <b>The aggregate eval cannot see this class of defect.</b> <c>spread</c> measures how
    /// concentrated a pool is, so it catches the channel collapsing into a popularity chart and is
    /// blind to a single under-evidenced title ranking first. This constant was set from a case
    /// found by reading real output, and there is no regression test at the eval level that would
    /// catch it coming back.
    /// </para>
    /// </summary>
    public double DegreeSmoothing { get; init; } = 20;

    /// <summary>
    /// Votes an edge needs before it counts at all. The distribution is savage — median 2, maximum
    /// 6008 — so the long tail is single-vote pairs, and one person clicking "recommend" is not
    /// evidence. This is also what stops <see cref="DegreePenalty"/> from promoting a degree-1 node
    /// whose one edge carries one vote: with a floor of 2 that edge is not in the sum to begin
    /// with.
    /// </summary>
    public int MinVotes { get; init; } = 2;

    /// <summary>
    /// Ceiling on how many graph-only candidates may be injected into the scoring pool per request.
    /// Half the pool's 200-row floor (<c>limit * 4</c> clamped to [200, 2000]), so the channel can
    /// widen retrieval substantially without outnumbering it.
    ///
    /// <para>
    /// <b>This, not <see cref="Weight"/>, is the channel's real intensity dial</b> — which is not
    /// what you would guess. Measured over the 24 synthetic profiles of
    /// <c>eval-reco.cs spread</c>, dropping the weight from 0.60 to 0.10 barely moved the share of
    /// picks the graph accounted for (82% to 70%), because a candidate that is in the pool at all
    /// is usually competitive on cosine anyway. Moving this cap is what actually changes the
    /// balance:
    /// </para>
    ///
    /// <code>
    ///  cap    genres  authors    tags  cohesion  overlap  co-read
    ///  off     31.46    50.88   591.4    0.6409        -       0%
    ///   50     32.12    50.21   637.6    0.6224    0.658      25%
    ///  100     32.50    50.38   667.8    0.6142    0.550      33%
    ///  300     32.96    49.42   727.4    0.5985    0.361      51%
    /// </code>
    ///
    /// <para>
    /// Every setting improves the over-fit metrics rather than worsening them — more distinct
    /// genres and tags, lower pairwise cohesion — so the usual objection to a collaborative channel
    /// (that it collapses into a popularity chart) does not hold here at any cap tested. 100 is
    /// chosen for the other reason: at 300 barely a third of the pre-channel pool survives, and a
    /// feature that ships on by default should refine an existing user's recommendations rather
    /// than replace them.
    /// </para>
    /// </summary>
    public int MaxInjected { get; init; } = 100;

    /// <summary>
    /// The cosine floor applied to a candidate the graph vouches for, in place of
    /// <c>SemanticRecommender.CosineFloor</c> (0.30).
    ///
    /// <para>
    /// A genuine cross-genre find is by definition a <em>low</em>-cosine candidate — that is the
    /// whole reason the embeddings missed it — so injecting graph neighbours into the pool and then
    /// applying the normal floor would discard exactly the results the feature exists to surface.
    /// It stays a floor rather than being removed outright so an unrelated title cannot ride in on
    /// one edge.
    /// </para>
    ///
    /// <para>
    /// <b>Measured inert on the libraries tested so far</b>, and worth saying so rather than
    /// implying it carries the feature: raising it back to 0.30 changed nothing at all on a real
    /// 14-series library, because the graph neighbours of a popular library turn out to be
    /// semantically near it anyway. It bites for an eclectic library whose co-reads genuinely do
    /// not describe alike, which is the case
    /// <c>SemanticRecommenderTests.AGraphBackedCandidate_BelowTheNormalCosineFloor_IsRecommendedAnyway</c>
    /// pins down. Cheap insurance, not the mechanism doing the visible work.
    /// </para>
    /// </summary>
    public double InjectedCosineFloor { get; init; } = 0.15;
}
