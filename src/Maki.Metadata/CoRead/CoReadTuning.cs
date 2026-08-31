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
/// It keeps an injection cap and a corroboration floor. <see cref="Weight"/> is the dial that
/// moves this channel and <see cref="MaxInjected"/> is inert, because this channel re-ranks
/// candidates the pool already holds rather than discovering new ones. The vote graph was once the
/// exact opposite - <c>MaxInjected</c> everything, <c>Weight</c> almost nothing - but on a denser
/// artifact its cap went inert as well and <c>RecoGraphTuning.MinInjectedScore</c> took over.
/// Neither channel is dialled by a cap any more.
/// </para>
///
/// <para>
/// Swept with <c>eval-reco.cs spread</c> over its 24 synthetic profiles, and re-swept against a
/// 19,667-user matrix and a 92-series install when the reader matrix doubled. That sweep is also what added the harness's <c>pop</c> column: every diversity metric
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
    /// <b>This is the channel's intensity dial.</b> It re-ranks candidates retrieval already
    /// pooled rather than injecting new ones, so <see cref="MaxInjected"/> stays inert at every
    /// value tested. The vote graph's dial used to be the opposite one (<c>MaxInjected</c>), but on
    /// a denser artifact that knob went inert too and its corroboration floor took over - see
    /// <c>RecoGraphTuning.MinInjectedScore</c>. Both channels now turn on a threshold, not a cap.
    /// </para>
    ///
    /// <para>
    /// Set from the popularity column rather than the diversity ones. Measured over the 24 synthetic
    /// profiles, median popularity rank of the returned pool (lower = more famous, out of ~126k
    /// ranked series). Left column is the 8,828-reader artifact this was first tuned against, right
    /// column the 19,667-reader one:
    /// </para>
    ///
    /// <code>
    ///  weight   pop @ 8.8k readers   pop @ 19.7k readers
    ///   off                   8196                  7084
    ///   0.05                     -                  6397
    ///   0.10                  7196                  6360
    ///   0.15                     -                  6140
    ///   0.20                  7135                  6026
    ///   0.25                     -                  5929
    ///   0.30                  6468                     -
    ///   0.60                  5465                     -
    ///   2.00                  3584                     -
    /// </code>
    ///
    /// <para>
    /// Read the diversity columns alone and higher weight looks better throughout: more tags, less
    /// cohesion. That reading is wrong, which is why the harness grew a <c>pop</c> column. On a real
    /// 14-series library the higher settings demoted genuinely obscure picks (a rank-21,260 title
    /// fell from position 6 to 11 at 0.6, and out of the top 15 entirely at 1.0) while promoting
    /// One Piece and Bleach.
    /// </para>
    ///
    /// <para>
    /// <b>0.15, lowered from 0.2 when the reader matrix roughly doubled.</b> Nothing about the
    /// channel changed; the extra coverage made the same coefficient louder, and 0.15 on the larger
    /// artifact reproduces the popularity profile 0.2 had on the smaller one. Below 0.1 the channel
    /// stops paying for itself and above 0.25 each further point of pick-share costs hundreds of
    /// ranks of skew.
    /// </para>
    ///
    /// <para>
    /// <b>Expect this to change nothing on a mainstream library, and know why.</b> Measured on a
    /// 92-series install, 0.15, 0.2 and 0.3 return a byte-identical pool, and the channel does not
    /// reorder anything at all until about 0.5 - with the vote graph on, switching this channel off
    /// entirely leaves the picks unchanged (overlap 1.000) even though it flags 62% of them. It
    /// earns its keep on libraries whose seeds the vote graph never reached, which is what the
    /// synthetic profiles model and what a popular library is not. Do not read "no effect here" as
    /// "no effect".
    /// </para>
    /// </summary>
    public double Weight { get; init; } = 0.15;

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
    /// channel's share of picks roughly in half (38% to 20% on the 8,828-user artifact, 37% to 17%
    /// on the 19,667-user one) while barely moving popularity, and by 0.15 the pool is
    /// byte-identical to the channel being switched off (overlap 1.000). Kept as a knob so the harness can re-check that
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
    ///
    /// <para>
    /// Re-checked once the behavioural channel started competing for the same pool slots, since a
    /// third injector could plausibly have made this one bind: it did not. On the independent
    /// grader a cap of 10 is within a thousandth of 50 and 200, and on held-out readers the three
    /// are indistinguishable. Contrast <c>TasteVectorTuning.MaxInjected</c>, which does bind.
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
