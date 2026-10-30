namespace Maki.Core.Recommendations;

/// <summary>
/// How hard a "so far" rating steers the seed centroid. A provisional score is the reader's opinion
/// partway through, so it pulls the weight toward the rating's own <c>rating / 5.0</c> by a
/// confidence below 1, and a final rating of the same number always steers harder.
/// <para>
/// The weight shrinks the rating's distance from neutral rather than blending it with the
/// behavioural weight. Behavioural weight on a long, recently read series saturates at
/// <see cref="TasteTuning.MaxWeight"/> (1.8), so a blend would lift a provisional 6 or 8 above the
/// same score finalised, and would have nothing to blend with when behavioural weighting is off.
/// </para>
/// <para>
/// None of this has an isolated measurement: the eval harness builds its own seed sets and never
/// runs <c>SeedWeightService</c>. The constants are a judgement call; check a change on
/// <c>.simulated</c>.
/// </para>
/// </summary>
public static class ProvisionalRatingPolicy
{
    /// <summary>Confidence at the first chapter.</summary>
    public const double MinConfidence = 0.35;

    /// <summary>Share of the confidence earned by read depth, reached at <see cref="DepthSaturationChapters"/>.</summary>
    public const double DepthShare = 0.30;

    /// <summary>
    /// Share earned by how far through a <em>finished</em> series the reader is. Zero for an ongoing
    /// series, so a caught-up reader of chapter 12 is not treated like someone who read the ending.
    /// </summary>
    public const double RatioShare = 0.15;

    public const double DepthSaturationChapters = 60;

    /// <summary>
    /// The confidence behind a provisional rating, in [0.35, 0.8]. <paramref name="read"/> is the
    /// progression count (<c>ReadCounts.ReadFor</c>, no watched chapters).
    /// <paramref name="totalChapters"/> only counts when <paramref name="completed"/>: an ongoing
    /// series' total is a moving number of chapters released so far.
    /// </summary>
    public static double Confidence(int read, int? totalChapters, bool completed)
    {
        var depth = read <= 0
            ? 0
            : Math.Min(1, Math.Log(1 + read) / Math.Log(1 + DepthSaturationChapters));
        var ratio = completed && totalChapters is > 0
            ? Math.Clamp((double)read / totalChapters.Value, 0, 1)
            : 0;
        return MinConfidence + DepthShare * depth + RatioShare * ratio;
    }

    /// <summary>The seed weight for a provisional rating of 5 or more, rounded like every other weight.</summary>
    public static double Weight(int rating, double confidence, TasteTuning tuning)
    {
        var weight = TasteWeights.Neutral + confidence * (rating / 5.0 - TasteWeights.Neutral);
        return tuning.WeightQuantum <= 0
            ? weight
            : Math.Round(Math.Round(weight / tuning.WeightQuantum) * tuning.WeightQuantum, 4);
    }

    /// <summary>
    /// The avoid strength for a provisional rating, the final rating's strength scaled by the
    /// confidence. Zero at or above the neutral point.
    /// </summary>
    public static double AvoidStrength(int rating, double confidence) =>
        RecommendationFeedbackPolicy.AvoidStrength(rating) * confidence;
}
