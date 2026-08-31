namespace Maki.Core.Recommendations;

/// <summary>
/// The constants behind behavioural seed weighting: how much a series the user actually read counts
/// for when the recommender builds its seed centroid. Same discipline as
/// <c>Maki.Metadata.Embedding.SearchTuning</c> — a record of named numbers with a
/// <see cref="Default"/>, registered as a singleton and never mutated at runtime, so the only thing
/// that ever varies them is the eval harness (<c>distribution/eval-reco.cs</c>).
/// <para>
/// Nothing here is a user preference. The whole feature is on or off per instance
/// (<c>recommendations.tasteweighting</c>); these numbers are the tuning surface behind that switch.
/// </para>
/// </summary>
/// <param name="DepthWeight">
/// Share of the evidence that comes from how many chapters were finished, in absolute terms. Depth
/// and ratio disagree usefully: a completed four-chapter one-shot is 100% read and almost no
/// evidence, while 180 chapters into a 400-chapter epic is 45% read and a great deal of it.
/// </param>
/// <param name="RatioWeight">Share from completed / downloaded — how far through the series they got.</param>
/// <param name="EngageWeight">
/// Share from time spent. Only ever applies when there <em>is</em> time data; see
/// <see cref="TasteWeights.Weight"/> for why a zero here means "unknown" rather than "none".
/// </param>
/// <param name="DepthSaturationChapters">
/// Chapter count at which the depth curve reaches 1. Log-shaped below it, so the first ten chapters
/// carry most of the signal and chapter 400 is not forty times chapter 10.
/// </param>
/// <param name="EngageSaturationMinutes">Minutes at which the engagement curve reaches 1.</param>
/// <param name="RecencyHalfLifeDays">
/// Days for the recency multiplier to fall halfway to <see cref="RecencyFloor"/>. Long by design:
/// taste drifts over years, not weeks, and a library is mostly read once.
/// </param>
/// <param name="RecencyFloor">
/// What an arbitrarily old read still counts for. Never 0 — somebody who read nothing this year
/// should still get recommendations shaped by what they read, not fall back to uniform weights.
/// Deliberately above <see cref="NeutralSignal"/>: at or below it, a series finished years ago
/// decays to exactly the weight of one never opened, and an old favourite is not the same evidence
/// as no evidence.
/// </param>
/// <param name="MinWeight">
/// Floor for the derived weight. Deliberately above zero: a series barely touched is weak evidence,
/// not evidence of dislike, and a seed that contributes nothing is a seed the user cannot see the
/// effect of.
/// </param>
/// <param name="MaxWeight">
/// Ceiling. Kept just under the 2.0 that a 10/10 rating produces (<c>rating / 5.0</c>) so an explicit
/// rating always outranks anything inferred, on the scale as well as by the blend rule.
/// </param>
/// <param name="NeutralSignal">
/// The signal value that maps to weight 1.0, i.e. today's behaviour. Below it a series is weighted
/// down, above it up. Low because most series in a real library sit near the bottom of the evidence
/// range and the point is to distinguish among them, not to weight the whole library down.
/// </param>
/// <param name="RatingBlendAlpha">
/// How much of a <em>rated</em> seed's weight comes from the rating: 1 = the rating wins outright and
/// behaviour only fills in unrated seeds, which is what ships. It exists as a number rather than an
/// <c>if</c> so the eval can explore partial blends without a code change.
/// </param>
/// <param name="WeightQuantum">
/// Rounding applied to the final weight, to snap out float noise so two runs meaning the same weight
/// produce the same text.
/// <para>
/// It must stay <em>well below</em> the resolution the pool cache key renders at (one decimal, see
/// <c>RecommendationService</c>). That key is what actually buys cache stability; this only has to be
/// fine enough to keep genuinely different histories apart. At 0.1 it was not: two series scoring
/// 1.6533 and 1.7215 both landed on 1.7, and <c>SemanticRecommender.PickRepresentativeSeeds</c> broke
/// the resulting tie with <c>ThenBy(id)</c> — so the more thoroughly read series lost the seed slot
/// that starts the farthest-point walk to whichever had the lower MangaBaka id, and that one choice
/// decides the whole pool. Measured on a real 92-series library: 64% of the top 40 changed depending
/// on which of the two tied first.
/// </para>
/// </param>
public sealed record TasteTuning(
    double DepthWeight = 0.40,
    double RatioWeight = 0.40,
    double EngageWeight = 0.20,
    double DepthSaturationChapters = 30,
    double EngageSaturationMinutes = 240,
    double RecencyHalfLifeDays = 240,
    double RecencyFloor = 0.5,
    double MinWeight = 0.4,
    double MaxWeight = 1.8,
    double NeutralSignal = 0.35,
    double RatingBlendAlpha = 1.0,
    double WeightQuantum = 0.01)
{
    public static readonly TasteTuning Default = new();

    /// <summary>
    /// Every channel off. What the instance switch resolves to when behavioural weighting is
    /// disabled, and the <c>uniform</c> baseline the eval compares against — both are "weight 1.0
    /// for every unrated seed", which is the behaviour that predates this feature.
    /// </summary>
    public static readonly TasteTuning Uniform =
        new(DepthWeight: 0, RatioWeight: 0, EngageWeight: 0);

    /// <summary>
    /// True when no channel carries any share, so every series would come back at the same weight
    /// and the aggregate is not worth running.
    /// </summary>
    public bool IsUniform => DepthWeight <= 0 && RatioWeight <= 0 && EngageWeight <= 0;
}
