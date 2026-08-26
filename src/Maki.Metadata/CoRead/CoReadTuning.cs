namespace Maki.Metadata.CoRead;

/// <summary>
/// The knobs behind the co-read channel: what AniList readers actually finished together, as
/// opposed to what they wrote recommendations about.
///
/// <para>
/// Deliberately <b>not</b> a copy of <c>RecoGraphTuning</c> with different numbers. Two of that
/// record's knobs have no meaning here and their absence is the point:
/// </para>
///
/// <list type="bullet">
/// <item><b>No degree penalty.</b> The co-read weight is already
/// <c>cooccurrence / sqrt((users(a)+k) · (users(b)+k))</c> — both endpoints' popularity divided out
/// at build time. Applying a degree penalty on top is the double normalization that was measured
/// on the vote graph at <c>DegreePenalty = 0.5</c>, where the channel inverted into degree-1
/// obscurities. <c>CoReadScorer</c> documents where that argument runs out, and why the answer is
/// still not a degree penalty.</item>
/// <item><b>No log compression.</b> Vote counts span 1 to 6008 and need it. Strengths are already
/// a bounded ratio; <c>log1p</c> of a number that small is very nearly the number itself, so it
/// would buy nothing and hide the scale.</item>
/// </list>
///
/// <para>
/// It keeps an injection cap and a corroboration floor, and <b>the two channels' dials turned out
/// to be opposite ones</b>: on the vote graph <c>MaxInjected</c> moved everything and
/// <c>Weight</c> almost nothing, here <see cref="Weight"/> moves everything and
/// <see cref="MaxInjected"/> is inert. That channel discovers, this one re-ranks.
/// </para>
///
/// <para>
/// Swept with <c>eval-reco.cs spread</c> over its 24 synthetic profiles, against the 8,828-user
/// matrix. That sweep is also what added the harness's <c>pop</c> column: every diversity metric
/// there said this channel was <em>improving</em> the pool while it was quietly turning it into a
/// popularity chart, because a set of famous titles spans plenty of genres and tags. See
/// <see cref="Weight"/>.
/// </para>
/// </summary>
public sealed record CoReadTuning
{
    public static readonly CoReadTuning Default = new();

    /// <summary>
    /// The channel's coefficient in <c>EmbeddingMath.HybridScore</c>, against a score normalized to
    /// [0,1].
    ///
    /// <para>
    /// <b>This is the channel's real intensity dial, and the vote graph's is not</b> — the exact
    /// opposite of that channel, where <c>MaxInjected</c> moved everything and <c>Weight</c> moved
    /// almost nothing. The reason is that the two channels work differently: the vote graph earns
    /// its keep by <em>injecting</em> candidates retrieval never pooled, while this one mostly
    /// <em>re-ranks</em> candidates already in the pool. With injection switched off entirely
    /// (<c>MaxInjected = 0</c>) this channel still accounts for 31% of picks.
    /// </para>
    ///
    /// <para>
    /// Set from the popularity column rather than the diversity ones. Measured over the 24 synthetic
    /// profiles, median popularity rank of the returned pool (lower = more famous, out of ~126k
    /// ranked series):
    /// </para>
    ///
    /// <code>
    ///  weight   co-read    tags  cohesion   median pop rank
    ///   off         0%   620.8    0.6323              8196
    ///   0.1        30%   639.0    0.6294              7196
    ///   0.2        31%   639.9    0.6292              7135
    ///   0.3        32%   644.3    0.6278              6468
    ///   0.6        38%   658.3    0.6222              5465
    ///   1.0        45%   671.1    0.6149              4253
    ///   2.0        59%   683.8    0.6027              3584
    /// </code>
    ///
    /// <para>
    /// Read the first three columns alone and higher is better throughout: more tags, less cohesion.
    /// That reading is wrong. The last column is monotone in the other direction, and on a real
    /// 14-series library the same settings demoted the genuinely obscure picks (a rank-21,260 title
    /// fell from position 6 to 11 at 0.6, and out of the top 15 entirely at 1.0) while promoting
    /// One Piece and Bleach.
    /// </para>
    ///
    /// <para>
    /// 0.2 because 0.1 to 0.2 is a plateau — the channel's share of picks moves 30% to 31% while
    /// popularity barely shifts — and 0.3 buys one further point of share for 667 ranks of skew.
    /// The point of this channel is finding what the embeddings miss, not resurfacing titles
    /// everybody has already heard of.
    /// </para>
    /// </summary>
    public double Weight { get; init; } = 0.2;

    /// <summary>
    /// Strength an edge needs before it counts at all.
    ///
    /// <para>
    /// Zero by default because the artifact is already filtered twice on the way out: an edge needs
    /// <c>minSupport</c> distinct users who finished both (3), and only each series' strongest
    /// <c>topPerItem</c> neighbours survive (60). Measured on the shipped matrix, a rank-60 edge
    /// still carries 48% of its series' best edge and a median of six users behind it, so there is
    /// no noise floor here to cut at — a second floor would only remove evidence the build already
    /// judged worth keeping.
    /// </para>
    ///
    /// <para>
    /// Swept, and it only ever removes evidence: 0.005 is indistinguishable from 0, 0.02 cuts the
    /// channel's share of picks from 38% to 20%, and by 0.15 the pool is byte-identical to the
    /// channel being switched off (overlap 1.000). Kept as a knob so the harness can re-check that
    /// on a future artifact, for the same reason the index stores weights raw rather than
    /// pre-compressed.
    /// </para>
    /// </summary>
    public double MinStrength { get; init; } = 0;

    /// <summary>
    /// Ceiling on how many co-read-only candidates may be injected into the scoring pool per
    /// request, on top of whatever the co-recommendation channel injects.
    ///
    /// <para>
    /// <b>Measured inert at the shipping floor.</b> Every value from 25 upward returns a
    /// byte-identical pool — 25, 50, 100, 200 and 400 all produce the same numbers — because fewer
    /// than 25 candidates ever clear <see cref="MinInjectedScore"/> in the first place. The floor
    /// binds; this does not. That is the reverse of the vote graph, where the cap was the only dial
    /// that mattered.
    /// </para>
    ///
    /// <para>
    /// Kept at 50 as headroom rather than lowered to the measured ceiling: it becomes live the
    /// moment the floor is lowered, and a knob that silently caps a future sweep is worse than one
    /// that currently does nothing. On a real 14-series library exactly one candidate was injected.
    /// </para>
    /// </summary>
    public int MaxInjected { get; init; } = 50;

    /// <summary>
    /// How strong a candidate's normalized co-read score must be, as a fraction of the strongest
    /// candidate's, before the channel may push it into the pool. Scoring is unaffected: anything
    /// ordinary retrieval already found keeps its full bonus however thin the evidence.
    ///
    /// <para>
    /// Higher than the vote graph's 0.60 because this graph is ten times denser (1.18M pairs against
    /// 116k) with up to 60 neighbours per series. Pool entry lets a candidate win on genre, tag,
    /// author and quality without ever having been near the cosine top-200 — the privilege that put
    /// an obscure one-shot at rank 1 of a real library before the vote graph got its floor — and a
    /// denser graph hands that privilege out far more readily at any threshold.
    /// </para>
    ///
    /// <para>
    /// Swept: 0.30 admits enough to replace nearly half the pool (overlap 0.552 against the channel
    /// being off) and drags median popularity rank down with it; 0.80 and above is barely
    /// distinguishable from off. 0.70 keeps overlap at 0.890 at the shipping weight, which is the
    /// "refine rather than replace" line the vote graph's cap was chosen on.
    /// </para>
    /// </summary>
    public double MinInjectedScore { get; init; } = 0.70;
}
