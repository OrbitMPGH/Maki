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
    ///
    /// <para>
    /// <strong>At 0.30 it drops nothing.</strong> Measured rather than assumed, after the value sat
    /// here long enough to be quoted as a live constraint by two other comments in this file:
    /// <c>cosinefloor=-1</c> and <c>cosinefloor=0.30</c> return byte-identical metrics over 800
    /// single-seed requests and again over 800 three-seed ones, in both attribution modes, and the
    /// recommender's survivor count equals its pool size at every floor up to 0.45. The reason is
    /// structural: the pool is the top slice of each query's cosine ranking, so by construction its
    /// members are the most similar rows in the catalogue and nothing in it is anywhere near 0.30.
    /// </para>
    ///
    /// <para>
    /// The floor only begins removing rows above 0.60, and what it costs there is not a property of
    /// the floor - it is entirely the <see cref="QueryAttribution.Standardized"/> interaction. Over
    /// 400 held-out libraries, raising it to 0.65 under <c>rawcosine</c> is free (-0.0008 nDCG@40,
    /// 95% [-0.0042, +0.0025], spans zero) while the same raise under the shipped <c>standardized</c>
    /// costs -0.0140 (95% [-0.0182, -0.0100]). Standardized scores a cosine that is no longer a
    /// maximum, so rows sit systematically lower against a fixed bar and a floor chosen under one
    /// mode rejects differently under the other.
    /// </para>
    ///
    /// <para>
    /// So this stays a guard against a pathological pool rather than a dial: at 0.30 it is below the
    /// interaction entirely and both modes agree, which is the property worth keeping. Anything
    /// reasoning about what it currently excludes should check that list is not empty first. See
    /// <see cref="CrowdBypassesCosineFloor"/>, which is a switch on a branch this never takes.
    /// </para>
    /// </summary>
    public double CosineFloor { get; init; } = 0.30;

    /// <summary>
    /// Whether a candidate a crowd graph vouched for may enter the ranking under
    /// <see cref="CosineFloor"/>.
    ///
    /// <para>
    /// The floor runs after injection, so a row the crowd channels put into the pool can be dropped
    /// moments later if the embeddings did not already find it plausible - which would cap the
    /// channel whose entire purpose is surfacing what the embeddings rank 40,000th, and spend
    /// <c>MaxInjected</c> on rows that cannot survive.
    /// </para>
    ///
    /// <para>
    /// On, and measured now rather than argued: <strong>at the shipped floor of 0.30 this setting
    /// does nothing whatsoever.</strong> The two mechanisms never meet. Over 800 single-seed requests
    /// graded on the vote graph the two values are byte-identical on every column, and identical
    /// again over 800 graded on the co-read graph, which is the other channel. Four seed sets
    /// instrumented directly agree and say why: the recommender's own counter reports <em>zero</em>
    /// crowd-backed rows dropped by the floor. Rank 40,000th by ranking is not the same as cosine
    /// below 0.30, and on 126k series almost nothing a crowd graph vouches for scores that low -
    /// readers pair titles that are usually semantically close too. See <see cref="CosineFloor"/>:
    /// at 0.30 that floor drops no rows at all, crowd-backed or otherwise, so there is no branch
    /// here to switch.
    /// </para>
    ///
    /// <para>
    /// Sweeping the floor upward finds where it would bite. The first crowd-backed row falls at
    /// 0.50, and the first change to a top ten at 0.55. What it readmits there is
    /// <c>Nisekoi: False Love</c>, <c>Horimiya</c>, <c>2.5 Dimensional Seduction</c> - well-known
    /// titles, not obscurities, and median popularity rank is unchanged in every case it fires
    /// except one where the bypass is the <em>more</em> famous of the two (1041 against 745). So it
    /// carries none of the drift toward thinly-tagged obscurities that
    /// <see cref="TagProfileSharpening"/> and <see cref="TagConsensusPower"/> did; readmitting a row
    /// the crowd already vouched for is a different operation from concentrating a profile.
    /// </para>
    ///
    /// <para>
    /// Left on, because where it fires it is neutral to mildly good and where it does not fire the
    /// value is irrelevant. The real finding is about the floor rather than this switch: at 0.30 it
    /// is not gating the crowd channels at all, which is worth knowing before anyone raises it. That
    /// interacts with <see cref="QueryAttribution.Standardized"/>, which scores a cosine that is no
    /// longer a maximum and so pushes rows down against a fixed floor. Eval knob:
    /// <c>crowdbypassesfloor</c>, and sweep it against <c>cosinefloor</c> rather than alone - alone
    /// it will keep reporting no difference.
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
    /// a list invented here. 1.0 weights every tag alike, which is what shipped before this.
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
    /// 2.0, down from the 3.0 this first shipped at, and the case for a non-1.0 value still does not
    /// rest on the eval - deliberately, with the reading rule fixed before any numbers were seen. The
    /// harness labels are co-read and co-recommendation graphs, which record what readers pair rather
    /// than what shares a premise, so they can bound this change's cost and cannot see its point.
    /// </para>
    ///
    /// <para>
    /// That cost depends on which population it is measured over, which the first pass here missed by
    /// only ever running one of them. Grading against held-out slices of real reading lists
    /// (<c>library</c> mode, 400 requests) the seeds are somebody's whole library, 16 to 20 titles,
    /// broad and famous skewed. Grading one seed at a time (<c>single</c> mode, 800 requests) the tag
    /// profile is that one series' own tag list rather than an aggregate, which is the "More like
    /// this" rail's situation and the sharpest test of a knob that reweights inside that profile.
    /// The single-seed population charges about three times as much:
    /// </para>
    ///
    /// <para>
    /// <c>1.5</c>: library -0.0009 (95% [-0.0026, +0.0008], spans zero), single -0.0040.
    /// <c>2.0</c>: library -0.0033 (95% [-0.0057, -0.0010]), single -0.0094.
    /// <c>3.0</c>: library -0.0057 (95% [-0.0091, -0.0024]), single -0.0158. Monotone throughout -
    /// there is no free step, 1.25 already measures -0.0019 on the narrow set.
    /// </para>
    ///
    /// <para>
    /// The benefit was measured on three seed sets chosen in advance, counting picks in the top ten
    /// that actually carry the premise the seeds share. Off it returns 11 of 30; 1.5 gives 16, 2.0
    /// gives 19, 3.0 gives 21. 3.0 is where it stops behaving: it costs the most in both modes while
    /// scoring <em>worse</em> than 2.0 on the cosplay set (5 on-premise against 6), so the top of the
    /// range is dominated rather than being the far end of a trade. 2.0 keeps 19 of the 21 for a
    /// little over half the cost.
    /// </para>
    ///
    /// <para>
    /// Read that on-premise count next to popularity and not on its own, which is the mistake
    /// <see cref="TagConsensusPower"/> shipped on: a title carrying the premise tag and little else
    /// scores on that count whether or not anyone would want it, and a thin tag list is what an
    /// obscure title has. Both directions are real here and they differ by population. Over whole
    /// libraries the picks get <em>more</em> famous (median rank 1518 to 1252 at 3.0), which is what
    /// the first pass saw and generalized from. Over narrow themed seeds they get less so - the three
    /// sets move 1159/536/10236 at 1.0 to 3899/2303/9879 at 3.0, and 3.0 is what pulls in
    /// <c>A Childhood Friend</c> at rank 236,681. At 2.0 the worst of that is 2069/2303/10236. Milder
    /// than the 44x consensus produced, and the same failure mode.
    /// </para>
    ///
    /// <para>
    /// Eval knob: <c>tagstoryboost</c>; pair it with a lower <c>Weights.Tag</c> to buy some of the
    /// cost back (3.0 with 1.5 measured -0.0036 in library mode), which is unmeasured on the seed
    /// sets and unmeasured on the narrow population.
    /// </para>
    /// </summary>
    public double TagStoryCategoryBoost { get; init; } = 2.0;

    /// <summary>
    /// How much of a tag's weight also credits its parent in MangaBaka's taxonomy, compounding per
    /// level up. 0 disables the mechanism entirely and scores exactly as before it existed.
    ///
    /// <para>
    /// The tag channel matches ids exactly, over a 2,493-tag vocabulary where a series carries a
    /// median of seven. That is sparse on purpose and sparse to a fault: two series that are the
    /// same kind of thing routinely share no id at all, because the taxonomy splits what they have
    /// in common one level below where they agree. Only the ROOT of <c>name_path</c> was ever kept,
    /// as <see cref="TagStoryCategoryBoost"/>'s category, so the four levels between root and leaf
    /// were read off the dump and thrown away at index time.
    /// </para>
    ///
    /// <para>
    /// Measured as a proxy before any of this was built: over 4,000 co-read pairs with support &gt;=
    /// 25 against 4,000 random pairs, the separation between "a crowd says these go together" and
    /// "these are unrelated" moves from Cohen's d <c>0.674</c> to <c>1.699</c> when each tag also
    /// credits its ancestors at 0.5 per level. That is a separation measure on a different question
    /// from nDCG, which is exactly why this ships at 0: the mechanism lands inert and the harness
    /// decides the default.
    /// </para>
    ///
    /// <para>
    /// Eval knob: <c>tagancestordecay</c>. Re-sweep <see cref="TagCandidateNormPower"/>,
    /// <see cref="TagStoryCategoryBoost"/> and <c>Weights.Tag</c> beside it rather than carrying
    /// them over. All three are calibrated against how dense a candidate's tag vector is, and this
    /// changes that: expansion multiplies a typical candidate's non-zero count by roughly four, and
    /// a root-level category boost is partly the same lever as a full-path decay.
    /// </para>
    /// </summary>
    /// <summary>
    /// How many series from one same-work component may appear in a result page. 0, which ships,
    /// disables the collapse entirely.
    ///
    /// <para>
    /// It is off because it was measured and it costs relevance. Capping one franchise to a single
    /// pick is <b>-0.0072 nDCG@40</b> over 300 held-out reading lists, bootstrap 95% [-0.0100,
    /// -0.0046]; also excluding the seeds' own franchises is <b>-0.0442</b>, 95% [-0.0532, -0.0357].
    /// Both intervals exclude zero.
    /// </para>
    ///
    /// <para>
    /// The premise was wrong, not the implementation. One pick in five sitting in a seed's franchise
    /// reads as a duplication defect, and it is not: readers who finish something go on to read the
    /// rest of it, so those picks are in the held-out set precisely because they were wanted. The
    /// app narrows this further still, since <c>RecommendationService</c> already excludes every
    /// series the caller owns - what survives into a real page is the franchise members they have
    /// NOT read, which is a recommendation rather than a repeat.
    /// </para>
    ///
    /// <para>
    /// Kept sweepable, in the same category as <see cref="TagAncestorDecay"/> and
    /// <c>SearchTuning.TagFloorAbsolute</c>: "surely we should stop showing people volume two" is
    /// the obvious hypothesis, it is wrong, and the answer is worth more with the code that produced
    /// it still present. Eval knobs: <c>maxperfranchise</c>, <c>excludeseedfranchise</c>.
    /// </para>
    /// </summary>
    public int MaxPerFranchise { get; init; }

    /// <summary>
    /// Whether a candidate sharing a franchise with any SEED is dropped outright, rather than merely
    /// capped against its siblings. Off, and the more expensive half of the finding recorded on
    /// <see cref="MaxPerFranchise"/>.
    ///
    /// <para>
    /// Separate knob because the two answer different complaints and cost differently: the cap stops
    /// one franchise eating a page, this stops the page answering something the Related rail already
    /// answered. Sweeping them together would have reported one number for two effects.
    /// </para>
    /// </summary>
    public bool ExcludeSeedFranchise { get; init; }

    /// <summary>
    /// Whether the credit channel counts the ARTIST as well as the writer. Off, because it measures
    /// inert and the reason it does is structural rather than a matter of tuning.
    ///
    /// <para>
    /// <c>artists</c> covers 98.3% of the recommendable catalogue and was read by nothing, which
    /// looked like an obvious gap: where the two columns differ, the artist is the half that decides
    /// what a series looks like, and no other channel carries that at all. They almost never differ.
    /// Union the two and single-seed nDCG@40 on the independent grader is 0.153 either way, and
    /// three-seed 0.158 either way, because on most rows the artist IS the author.
    /// </para>
    ///
    /// <para>
    /// Applied at query time rather than at index build, since the index is shared by every eval
    /// variant in one run and a knob baked into it would force a rebuild per variant. The sentinel
    /// filtering cannot be a knob for that reason and is unconditional at index build: a value that
    /// is not a person is not a credit, and <c>"Anthology"</c> is the single most common value in
    /// the column. Eval knob: <c>creditartists</c>.
    /// </para>
    ///
    /// <para>
    /// Worth re-reading with the author channel itself. Turning that channel off entirely
    /// (<c>wauthor=0</c>) now measures <b>+0.0045 nDCG</b> at three seeds, 95% [+0.0008, +0.0081] -
    /// the opposite of what it measured before the behavioural channel existed, which is consistent
    /// with that channel already knowing who made what. One weak result on one label set is not
    /// enough to retune a coefficient on, and the tag phase in this file is what happens when it is.
    /// </para>
    /// </summary>
    public bool CreditsIncludeArtists { get; init; }

    /// <summary>
    /// Weight multiplier on tags describing how a series is PACKAGED and who it is for -
    /// <c>Work Info</c> (publication medium, page layout, art style, colour) and
    /// <c>Audience Demographics</c>. 1.0 leaves them at par, which is what shipped.
    ///
    /// <para>
    /// <see cref="TagStoryCategoryBoost"/> deliberately excludes both, and correctly for the question
    /// it answers: they are the least premise-bearing categories in the vocabulary. But the thing
    /// this engine is asked for is "feels like", and format is a large part of that - a longstrip
    /// webtoon returned for a tankoubon seed reads wrong however well the premise matches. The feel
    /// metrics say it is the weakest column there is: format agreement runs 11-25% against
    /// demographic agreement at 69-89%.
    /// </para>
    ///
    /// <para>
    /// A separate dial rather than a wider story set, because the two are answering different
    /// questions and a single number could not be right for both. Eval knob: <c>tagformatboost</c>.
    /// </para>
    /// </summary>
    public double TagFormatCategoryBoost { get; init; } = 1.0;

    public double TagAncestorDecay { get; init; }

    /// <summary>
    /// Whether a tag also emits its own full path as an ancestor node at weight 1.
    ///
    /// <para>
    /// Without it, a series tagged <c>Themes &gt; Romance</c> and one tagged
    /// <c>Themes &gt; Romance &gt; Harem</c> meet only at <c>Themes</c>: the first carries Romance
    /// as a tag id, the second as a path prefix, and those are different keys. With it they also
    /// meet at <c>Themes &gt; Romance</c>, at the price of counting every exact match twice and so
    /// shifting the balance between exact and approximate agreement.
    /// </para>
    ///
    /// <para>
    /// Off because the measurement quoted on <see cref="TagAncestorDecay"/> was taken without it.
    /// Eval knob: <c>tagancestorself</c>; only does anything while the decay is above 0.
    /// </para>
    /// </summary>
    public bool TagAncestorIncludesSelf { get; init; }

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
    /// <strong>1.0, meaning off. It was briefly 3.0 and the sweep took it straight back out</strong>,
    /// against a threshold fixed before the numbers were seen. nDCG@40 0.124 to 0.045 at 3.0 over
    /// 400 libraries (paired 95% [-0.0876, -0.0720]), MRR 0.331 to 0.114, hit rate 86% to 43%, and
    /// already -0.0477 at 2.0. Worse than <see cref="TagProfileSharpening"/>, which is the thing it
    /// was designed not to be.
    /// </para>
    ///
    /// <para>
    /// The reasoning that failed is worth keeping, because it was nearly right. Consensus really
    /// does carry no rarity - <c>TagMathTests</c> pins two equally-agreed tags holding their ratio
    /// across a sixteenfold IDF difference - so it is not sharpening's mechanism. But it shares
    /// sharpening's <em>consequence</em>: any operation that concentrates the profile onto a few
    /// tags is best matched by candidates carrying those tags and almost nothing else, because
    /// <see cref="TagMath.Score"/> divides by the candidate's own norm. Thin tag lists belong to
    /// obscure titles, and median popularity rank went 1245 to 54534 - forty-four times further out
    /// than the catalogue this serves. Concentration is the hazard, not rarity, and
    /// <see cref="TagCandidateNormPower"/> only damps the division rather than removing it.
    /// </para>
    ///
    /// <para>
    /// The seed-set evidence that argued for it was real and still misleading: on-premise counts ran
    /// 21 of 30 to 28 of 30, because a title carrying the premise tag and little else scores well on
    /// that count whether it is a good recommendation or an unread one. Counting whether a pick
    /// matches is not the same as asking whether it is worth showing, and the popularity column is
    /// the check that separates them. Eval knob: <c>tagconsensus</c>, kept so the finding stays
    /// reproducible.
    /// </para>
    /// </summary>
    public double TagConsensusPower { get; init; } = 1.0;
}
