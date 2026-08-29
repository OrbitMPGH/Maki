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
/// Which of <see cref="SemanticRecommender"/>'s query vectors is credited with a candidate, once the
/// scan has scored that candidate against all of them. Decides both what the semantic channel scores
/// and which seed <c>MangaBakaRecommendation.BecauseOfTitle</c> names.
/// </summary>
public enum QueryAttribution
{
    /// <summary>
    /// Highest raw cosine wins, and that cosine is what the semantic channel scores. What shipped.
    ///
    /// <para>
    /// Comparing raw cosines across queries assumes the queries sit on one scale, and they do not.
    /// The centroid is L2-normalized after summing (<see cref="EmbeddingMath.WeightedMean"/>), so
    /// <c>cos(centroid, x) = sum_i cos(s_i, x) / norm(sum_i s_i)</c>. Seeds are not orthogonal - an
    /// embedding space this anisotropic puts every catalogue vector in one cone - so for mean
    /// pairwise seed cosine <c>p</c> the denominator is about <c>n * sqrt(p)</c> and the whole
    /// expression is the <em>average</em> per-seed cosine divided by <c>sqrt(p)</c>. The centroid
    /// channel therefore sits above every seed channel by a roughly constant factor (about 1.4 at
    /// <c>p = 0.5</c>) that is a normalization artifact rather than evidence about any candidate.
    /// </para>
    ///
    /// <para>
    /// It does not win everywhere - a candidate distinctively close to one seed still beats it, if
    /// it beats it by more than that factor. What it does win is the generic middle, which is most
    /// of the catalogue, so the semantic term systematically prefers a row close to everything the
    /// reader likes over a row very close to one thing, and <c>BecauseOfTitle</c> goes missing on
    /// the mixed libraries a per-title explanation would say the most about.
    /// </para>
    /// </summary>
    RawCosine,

    /// <summary>
    /// The query with the highest per-query z-score wins, and <em>its</em> raw cosine is what the
    /// semantic channel scores. Asks which query finds this candidate unusually similar rather than
    /// which query is scaled highest, which is the question "feels like X" is an answer to.
    ///
    /// <para>
    /// Moves the ranking: the semantic term now follows the query that finds the row unusual rather
    /// than the one scaled highest. What it does <em>not</em> decide by itself is the label -
    /// <see cref="RecommenderTuning.AttributionMargin"/> owns that, and at a margin of 0 this mode
    /// names a seed on nearly every row, which is as uninformative as naming one on nearly none.
    /// Standardizing makes the comparison fair; the margin is what makes it mean something. It is
    /// also no longer a maximum, so
    /// the cosine it scores is systematically lower than <see cref="RawCosine"/>'s, the semantic term
    /// shrinks against the structured channels, and more rows fall under
    /// <see cref="RecommenderTuning.CosineFloor"/>. Sweep the floor together with this, not after it.
    /// </para>
    /// </summary>
    Standardized,

    /// <summary>
    /// The z-score picks which seed is named; the raw maximum still scores. Ranking is identical to
    /// <see cref="RawCosine"/> row for row, which is what makes this the control: it prices the
    /// attribution change on its own, before <see cref="Standardized"/> moves the results underneath
    /// it and the two effects stop being separable.
    /// </summary>
    StandardizedLabelOnly,
}

/// <summary>
/// How <see cref="RecommenderTuning.AttributionMargin"/> is read: as a fixed number, or as a
/// position in the pool's own spread.
/// </summary>
public enum AttributionScale
{
    /// <summary>
    /// The margin is a raw distinctiveness difference. What shipped, and what does not survive a
    /// change of library size. Measured on one real library resampled to 10, 20, 46 and 92 seeds,
    /// the mean gap climbs 0.385 / 0.502 / 0.601 / 0.747 while its spread barely moves, sitting near
    /// 0.25 throughout - so a fixed bar does not stay in the same place in the distribution, it
    /// walks through it. A margin of 0.5 named 26% of the top 200 candidates at 10 seeds and 85% at
    /// 92. Kept so the eval can reproduce the old behaviour and compare the two on one run.
    /// </summary>
    Absolute,

    /// <summary>
    /// The margin is a count of standard deviations of the returned candidates' distinctiveness. Asks
    /// whether a candidate stands out among the candidates it is being shown beside, which is both
    /// the question the label is answering and the one whose answer holds still: over the same
    /// resampling, a margin of 0.5 standard deviations named 21.5% / 25.5% / 27% / 26% at 10, 20, 46
    /// and 92 seeds. It tracks the mean the absolute scale walks away from.
    /// </summary>
    PoolRelative,
}

/// <summary>
/// The knobs on <see cref="SemanticRecommender"/> that are not a channel coefficient. Broken out
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
    /// <strong>On, and the only default in this record with no measurement behind it.</strong> It
    /// shipped false; it was turned on by hand, and the eval sweep the previous version of this
    /// comment asked for was never run. So the paragraph above is still an open question rather than
    /// a settled one, and the channel is currently answering it in the permissive direction on
    /// nothing but argument. <c>crowdbypassesfloor=false</c> in
    /// <c>distribution/eval-reco-labels.cs</c> is the baseline to read it against; do that before
    /// treating this value as decided.
    /// </para>
    /// </summary>
    public bool CrowdBypassesCosineFloor { get; init; } = true;

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
    /// 48, which is the top of that curve and the most expensive point on it: 534 ms per pool build
    /// against 225 at 16. The numbers above are unchanged and still say the curve is logarithmic
    /// while the price is linear - what changed is the answer to the trade, not the evidence for it.
    /// This is the deliberate purchase the old comment described as one, taken because the pool is
    /// cached for twelve hours per user, so the cost lands on one page load per user per half-day.
    /// If that page load ever starts mattering, this is the first knob to spend, and 24 keeps most
    /// of the relevance for well under half the time.
    /// </para>
    /// </summary>
    public int MaxSeedQueries { get; init; } = 48;

    /// <summary>
    /// Which seeds those are. See <see cref="SeedSelection"/>; only has any effect on a library
    /// holding more than <see cref="MaxSeedQueries"/> seeds, since below that every seed is queried
    /// and the strategy cannot matter — so it never touches the "More like this" rail or a small
    /// seeded Discover.
    ///
    /// <para>
    /// <see cref="SeedSelection.Medoid"/>, and on relevance that is a coin flip: over 800 real
    /// libraries medoid beat farthest by +0.005 MRR with a bootstrap interval of [-0.007, +0.018],
    /// which spans zero. The reason to prefer it anyway is a question relevance cannot answer.
    /// Farthest-point sampling returns the seed set's hull rather than its centres of mass
    /// (<c>SeedSelectionTests</c> pins exactly that), so its per-seed queries are the oddest things
    /// in the library, and a query that wins on one of those produces "feels like &lt;the strangest
    /// thing you ever read&gt;". Medoid spends the same budget on one query per cluster the library
    /// actually has, so when a per-seed query does win, the title it names is one the reader would
    /// recognise as representative.
    /// </para>
    ///
    /// <para>
    /// That last step is an argument, not a measurement: no eval here scores whether a named title
    /// is a <em>good</em> explanation, only how often one is named at all. The switch is free on the
    /// numbers that exist and better on the one that does not.
    /// </para>
    /// </summary>
    public SeedSelection SeedSelection { get; init; } = SeedSelection.Medoid;

    /// <summary>
    /// How a candidate picks which query it is scored and explained by. See
    /// <see cref="Embedding.QueryAttribution"/>.
    ///
    /// <para>
    /// <see cref="QueryAttribution.Standardized"/>. Against the shipped
    /// <see cref="QueryAttribution.RawCosine"/> it measured indistinguishable on relevance over 100
    /// real libraries - +0.0013 nDCG@40, bootstrap 95% [-0.0082, +0.0110] - while taking the share
    /// of picks that can name a seed from 12% to 100% at a margin of 0. It also returns slightly
    /// less famous picks (median popularity rank 5788 to 6716), which this label set penalises for
    /// reasons unrelated to quality, so the flat relevance is probably a floor rather than a ceiling.
    /// </para>
    ///
    /// <para>
    /// Unlike <see cref="SeedSelection"/> this one is not confined to large libraries: the centroid
    /// competes with the per-seed queries at every seed count above one, so it moves the "More like
    /// this" rail and a small seeded Discover as well. And it is only half a decision -
    /// <strong>this mode with <see cref="AttributionMargin"/> left at 0 names a seed on essentially
    /// every row</strong>, which is the failure mode that motivated the margin in the first place.
    /// The two are set together or not at all.
    /// </para>
    /// </summary>
    public QueryAttribution QueryAttribution { get; init; } = QueryAttribution.Standardized;

    /// <summary>
    /// How much better the best single seed has to explain a candidate than the whole library does
    /// before that seed is allowed to name it, in the units
    /// <see cref="Embedding.QueryAttribution"/> is crediting in: a raw cosine difference under
    /// <see cref="QueryAttribution.RawCosine"/>, standard deviations once the channels are measured.
    /// Below the margin the candidate carries no <c>BecauseOfTitle</c> and the UI says nothing,
    /// which is the correct answer for a row that is simply a good fit for the reader in general.
    ///
    /// <para>
    /// Zero reproduces a plain argmax, which is what both attribution modes did before this existed:
    /// under <see cref="QueryAttribution.RawCosine"/> the centroid's normalization offset wins most
    /// rows and almost nothing gets named, and under <see cref="QueryAttribution.Standardized"/> the
    /// offset is gone but N seed queries now face one centroid, so nearly everything gets named
    /// instead. Both numbers are artifacts of how many queries there are. Neither is a statement
    /// about any candidate.
    /// </para>
    ///
    /// <para>
    /// <strong>Zero is therefore no longer a safe default</strong>, now that
    /// <see cref="QueryAttribution"/> ships as <see cref="QueryAttribution.Standardized"/>: it is
    /// the "name a seed on every row" setting. Measured over 100 real libraries at that mode, the
    /// share of picks carrying a title runs 100% / 81% / 42% / 24% / 16% at margins of
    /// 0 / 1 / 2 / 3 / 4, with every relevance column identical to three decimals across all of
    /// them - the margin is genuinely free, so the value is a product decision about how often the
    /// rail should claim a reason, not a relevance trade.
    /// </para>
    ///
    /// <para>
    /// 0.5, read as standard deviations because <see cref="AttributionScale"/> defaults to
    /// <see cref="AttributionScale.PoolRelative"/>. That lands around a quarter of the top
    /// candidates naming a seed, and stays there as a library grows: 21.5% / 25.5% / 27% / 26% over
    /// one real library resampled to 10, 20, 46 and 92 seeds. Roughly 0 is the loudest useful
    /// setting (~44%) and 1.0 a quiet one (~15%); the scale is a position in a distribution, so
    /// values much past 2 name nobody.
    /// </para>
    ///
    /// <para>
    /// Under <see cref="AttributionScale.Absolute"/> the same field is a raw gap instead, where the
    /// useful range is roughly 0 to 1 and does not hold still across library sizes. A value swept
    /// under one scale means nothing under the other.
    /// </para>
    ///
    /// <para>
    /// There is no principled value to derive here, only a calibration: sweep it against the eval's
    /// <c>named</c> column until the share of attributed picks is what the rail should show, and
    /// read <c>nDCG</c> beside it to confirm the gate is not being paid for in relevance. Because
    /// the units differ per mode, a margin tuned under one is meaningless under the other.
    /// </para>
    /// </summary>
    public double AttributionMargin { get; init; } = 0.5;

    /// <summary>
    /// Whether <see cref="AttributionMargin"/> is a raw difference or a position in the pool's own
    /// spread. See <see cref="Embedding.AttributionScale"/>; defaults to
    /// <see cref="AttributionScale.PoolRelative"/>, which is what makes one margin work across
    /// libraries of different sizes.
    /// </summary>
    public AttributionScale AttributionScale { get; init; } = AttributionScale.PoolRelative;

    /// <summary>
    /// Exponent applied to the seed tag profile's weights before
    /// <see cref="TagMath.Score"/> compares a candidate against it. 1.0 is the plain profile.
    ///
    /// <para>
    /// It exists because raising <c>Weights.Tag</c> could not fix what it looked like it should.
    /// <see cref="TagMath.Score"/> is a cosine over the candidate's whole tag list, so it rewards
    /// overlapping with <em>many</em> profile tags rather than with the important ones, and a seed
    /// set chosen for one premise still carries a long tail of tropes most of the genre shares. On
    /// three "forced cohabitation" seeds the profile correctly put <c>Cohabitation</c> and
    /// <c>Arranged Marriage</c> at the top, and a candidate carrying neither still outscored three
    /// that carried both - on <c>Love Triangle</c>, <c>Tsundere</c> and <c>Partial Nudity</c>, all
    /// real entries further down the same profile. Turning the tag coefficient up amplified that
    /// noise exactly as much as the signal, which is why it moved the tail of the rail and not its
    /// head.
    /// </para>
    ///
    /// <para>
    /// <strong>1.0, meaning off, because raising it is much worse than the problem it addresses.</strong>
    /// On the seed set above it does what it promises - the premise-less candidate falls from third
    /// to fourth on the tag channel while the one matching all three top profile tags climbs from
    /// 0.356 to 0.544 - but swept over real libraries it collapses the recommender: nDCG@40 0.119 to
    /// 0.063 at 3.0 (paired 95% [-0.0615, -0.0495]), MRR 0.310 to 0.156, hit rate 83% to 56%, and
    /// already -0.037 at 2.0. Monotone, and nowhere near zero.
    /// </para>
    ///
    /// <para>
    /// The popularity column says why, and it is not "the labels prefer mainstream": median
    /// popularity rank goes from 3466 to 32146, a tenfold move into the obscure.
    /// <see cref="TagMath.Score"/> divides by the candidate's own tag norm, so a profile concentrated
    /// onto two or three tags is best matched by series that carry those tags and almost nothing
    /// else - which is what a thinly tagged niche title is. A well-known series is penalised for
    /// having a full tag list. Sharpening therefore does not buy premise specificity, it buys
    /// sparseness, and the one hand-checked seed set that looked fixed was a coincidence of that.
    /// </para>
    ///
    /// <para>
    /// Kept as a knob rather than deleted so the finding stays reproducible (<c>tagsharpening</c> in
    /// <c>distribution/eval-reco-labels.cs</c>), and because the underlying complaint is real and
    /// still open: a seed set with one specific premise still gets recommendations that share its
    /// genre and not its setup. Fixing that means addressing the candidate-side normalization -
    /// damping the norm, or scoring against the top of the profile rather than all of it - not
    /// reweighting a cosine that rewards sparseness.
    /// </para>
    /// </summary>
    public double TagProfileSharpening { get; init; } = 1.0;

    /// <summary>
    /// Exponent on the candidate's own tag norm in <see cref="TagMath.Score"/>. 1.0 is the plain
    /// cosine that shipped; lower values soften how much a candidate is penalised for carrying tags
    /// the seed profile never asked about.
    ///
    /// <para>
    /// This is the second attempt at the same complaint <see cref="TagProfileSharpening"/> failed to
    /// fix, aimed at the mechanism rather than around it. A cosine divides the overlap by the
    /// candidate's whole tag list, so a series with a rich tag list is charged for its richness while
    /// a thinly tagged one is not - which is why concentrating the profile made the recommender
    /// collapse onto obscure titles instead of onto the premise. Damping the divisor decouples
    /// "matches what the seeds are about" from "has few tags".
    /// </para>
    ///
    /// <para>
    /// 0.75, paired with <c>Weights.Tag = 2.0</c>. Below 1 the channel is no longer a cosine in
    /// [0,1], so the two were swept as a grid rather than one after the other - and that mattered:
    /// at 0.75 the damping is worth +0.0115 nDCG@40 with the weight corrected to 2.0 (95% [+0.0076,
    /// +0.0155], replicated at +0.0121 on a disjoint sample) and an indistinguishable +0.0042 with
    /// the weight left at 4.5. A 0.5 power with the weight uncorrected measured actively
    /// <em>worse</em>, which is the scale confound rather than the idea failing. Eval knob:
    /// <c>tagnormpower</c>.
    /// </para>
    ///
    /// <para>
    /// <strong>The number is not why this shipped.</strong> Median popularity rank goes 4081 to 1273
    /// alongside that gain, and this label set rewards famous picks for reasons unrelated to
    /// quality - so the metric cannot separate "better" from "more popular", and the mechanism says
    /// the two arrive together: a well-known series carries more tags because more people tagged it,
    /// and a plain cosine charges it for every one the seeds did not ask about. It shipped because
    /// that is an indefensible thing for the ranking to do - a series should not place lower because
    /// the dump has better coverage of it - and the measurement is corroboration rather than the
    /// case. It costs a little theme specificity on a narrowly themed seed set, which is the
    /// trade-off, not a bug.
    /// </para>
    /// </summary>
    public double TagCandidateNormPower { get; init; } = 0.75;

    /// <summary>
    /// Multiplier on tags whose MangaBaka category describes the story rather than its cast - see
    /// <see cref="TagMath.CategoryWeight"/> for the split, which is the dump's own taxonomy and not
    /// a list invented here. 1.0 weights every tag alike, which is what shipped.
    ///
    /// <para>
    /// The others in this area reweight a channel; this one reweights inside it, and it is the only
    /// attempt so far that has the information needed to. A seed set chosen for one premise carries
    /// its premise tags (<c>Themes &gt; Cohabitation</c>) alongside a long tail of tropes its whole
    /// genre shares (<c>Character Archetype &gt; Dere Types</c>, <c>Sexual Content &gt; Nudity</c>),
    /// and nothing in the score could tell those apart: IDF ranks by rarity, and the trope tags are
    /// frequently rarer than the premise ones. Turning the channel up amplified both, and
    /// concentrating the profile bought sparseness instead - see
    /// <see cref="TagProfileSharpening"/> for that measurement.
    /// </para>
    ///
    /// <para>
    /// 3.0, and it is the one default here whose case does not rest on the eval - deliberately, and
    /// with the reading rule fixed before the numbers were seen. The harness labels are co-read and
    /// co-recommendation graphs, which record what readers pair rather than what shares a premise,
    /// so they can bound this change's cost and cannot see its point. That cost is real but small:
    /// -0.0050 nDCG@40 at 3.0 over 400 libraries (95% [-0.0080, -0.0022]), against -0.0554 for the
    /// sharpening attempt that had to be abandoned. Median popularity rank moves 1518 to 956, so it
    /// returns better-known titles rather than the thinly tagged ones sharpening collapsed onto.
    /// </para>
    ///
    /// <para>
    /// The benefit was measured on three seed sets chosen in advance, counting picks in the top ten
    /// that actually carry the premise the seeds share. Cohabitation 5 to 9, cosplay 4 to 5, and
    /// childhood-friends-turned-lovers 1 to 6 - that last set returning a single on-premise title in
    /// ten with the boost off, which is the complaint this whole line of work started from. Ten of
    /// thirty becomes twenty of thirty. Higher still keeps paying on the sharpest sets (4.0 takes
    /// childhood friends to 8) for another -0.001, so the exact value is a judgement rather than an
    /// optimum. Eval knob: <c>tagstoryboost</c>; pair it with a lower <c>Weights.Tag</c> to buy some
    /// of the cost back (3.0 with 1.5 measured -0.0036), which is unmeasured on the seed sets.
    /// </para>
    /// </summary>
    public double TagStoryCategoryBoost { get; init; } = 3.0;

    /// <summary>
    /// Exponent on how much of the seed set agreed about a tag, applied before IDF and category are
    /// folded in. 1.0 prices agreement linearly, which is what shipped.
    ///
    /// <para>
    /// A seed set usually shares more than one thing, and what it shares <em>together</em> is closer
    /// to its premise than any single tag is. Four childhood-friend romcoms share
    /// <c>Childhood Friends</c>, <c>Romance</c>, <c>Comedy</c>, <c>Slice of Life</c> and
    /// <c>Heterosexual</c>; a candidate carrying the set is a better match than one carrying the
    /// relationship tag alone, and linear pricing puts a four-of-four tag only four times a
    /// one-of-four tag. Raising the power widens that.
    /// </para>
    ///
    /// <para>
    /// It is not <see cref="TagProfileSharpening"/> under a new name, which is worth being explicit
    /// about given that one had to be abandoned. Sharpening exponentiates the finished weight, so it
    /// rewards rare tags exactly as much as agreed-on ones and collapsed the recommender onto
    /// thinly tagged titles. This exponentiates only the agreement share, which carries no rarity at
    /// all.
    /// </para>
    ///
    /// <para>
    /// 3.0, which puts a unanimous tag eight times a half-agreed one where linear pricing put it
    /// twice. Measured on the same three seed sets <see cref="TagStoryCategoryBoost"/> was judged on,
    /// counting picks in the top ten that carry the premise the seeds share: cohabitation 9 to 10,
    /// cosplay 5 to 8, childhood friends 7 to 10, so twenty-one of thirty becomes twenty-eight. The
    /// cosplay set is the telling one - two seeds sharing <c>Cosplay</c> and <c>Otaku</c> and
    /// <c>Gyaru</c> is a far sharper signature than any of the three alone, and it is the set the
    /// category boost moved least.
    /// </para>
    ///
    /// <para>
    /// It can do nothing for a single seed, where every tag is unanimous by definition, so it moves
    /// multi-seed Discover only and leaves the "More like this" rail exactly as it was. Eval knob:
    /// <c>tagconsensus</c>. <strong>Its cost on the harness labels had not come back when this
    /// shipped</strong> - the benefit above is the whole case, on the reading rule
    /// <see cref="TagStoryCategoryBoost"/> sets out. If a sweep puts it near that knob's -0.005 it
    /// is the expected trade; anywhere near <see cref="TagProfileSharpening"/>'s -0.055 and it
    /// should come back out.
    /// </para>
    /// </summary>
    public double TagConsensusPower { get; init; } = 3.0;
}
