namespace Maki.Core.Recommendations;

/// <summary>
/// What one series looks like in one user's reading history, reduced to the four numbers the weight
/// function needs. Built by <c>BehavioralTasteService</c> from <c>ChapterProgress</c>; kept as a
/// plain record here so the maths has no EF dependency and the eval harness can synthesise inputs.
/// </summary>
/// <param name="Completed">Chapters of this series the user finished, counted the <c>ReadCounts</c> way.</param>
/// <param name="Downloaded">Chapters of this series that exist on disk — the denominator for "how far through".</param>
/// <param name="Seconds">
/// Time banked against those chapters. <strong>Zero means unknown, not zero.</strong> Kavita-imported
/// rows and OPDS reads never carry time, so a zero here has to be treated as a missing channel rather
/// than as evidence of a bounce.
/// </param>
/// <param name="LastReadAt">Most recent progress write, for the recency multiplier. Null = never read.</param>
public readonly record struct SeriesReadSignal(
    int Completed,
    int Downloaded,
    long Seconds,
    DateTime? LastReadAt);

/// <summary>
/// Turns reading behaviour into a seed weight on the same scale the recommender already takes from
/// explicit ratings (<c>rating / 5.0</c>: 1.0 is neutral, higher pulls the seed centroid toward the
/// series, lower away).
/// <para>
/// Pure and deterministic — the date is passed in rather than read, both so it is testable and so the
/// caller can pass a <em>date</em> instead of an instant. That matters: the weights land in
/// <c>RecommendationService</c>'s pool cache key, so recency has to step once a day rather than drift
/// continuously, or the cache never hits.
/// </para>
/// </summary>
public static class TasteWeights
{
    /// <summary>The weight of a seed nothing is known about, and the value a rating of 5/10 produces.</summary>
    public const double Neutral = 1.0;

    /// <summary>
    /// The seed weight this reading history implies, rounded to <see cref="TasteTuning.WeightQuantum"/>.
    /// </summary>
    /// <param name="typeAffinity">
    /// 0…1: how much this series' type matches what the user mostly reads, where 1 is their most-read
    /// type. Defaults to 1 (no effect) for callers that do not compute it, which at the shipped
    /// <see cref="TasteTuning.TypeAffinityWeight"/> of 0 is every caller.
    /// </param>
    public static double Weight(
        SeriesReadSignal signal, DateOnly today, TasteTuning tuning, double typeAffinity = 1.0)
    {
        if (tuning.IsUniform || signal.Completed <= 0)
        {
            return Neutral;
        }

        var depth = Curve(signal.Completed, tuning.DepthSaturationChapters);
        var ratio = signal.Downloaded > 0
            ? Math.Clamp((double)signal.Completed / signal.Downloaded, 0, 1)
            : 0;

        // The engagement channel only exists when this series has time on it. Dropping it (rather
        // than scoring it 0) is the difference between "we don't know" and "they bounced": a
        // Kavita-imported library has zero seconds on every row, and scoring those as no engagement
        // would push the entire library to MinWeight and leave the feature worse than uniform.
        var hasTime = signal.Seconds > 0;
        var total = tuning.DepthWeight + tuning.RatioWeight + (hasTime ? tuning.EngageWeight : 0);
        if (total <= 0)
        {
            return Neutral;
        }

        var evidence = (tuning.DepthWeight * depth + tuning.RatioWeight * ratio) / total;
        if (hasTime)
        {
            var engage = Curve(signal.Seconds / 60.0, tuning.EngageSaturationMinutes);
            evidence += tuning.EngageWeight * engage / total;
        }

        evidence *= 1 - tuning.TypeAffinityWeight + tuning.TypeAffinityWeight * Math.Clamp(typeAffinity, 0, 1);

        var raw = Map(evidence * Recency(signal.LastReadAt, today, tuning), tuning);
        return Quantize(raw, tuning.WeightQuantum);
    }

    /// <summary>
    /// The weight a seed ends up with once an explicit rating is taken into account.
    /// <see cref="TasteTuning.RatingBlendAlpha"/> of 1 short-circuits to the rating itself, so the
    /// shipped configuration cannot perturb a rated seed by so much as a rounding error.
    /// </summary>
    public static double Blend(double ratingWeight, double behaviouralWeight, TasteTuning tuning)
    {
        var alpha = Math.Clamp(tuning.RatingBlendAlpha, 0, 1);
        return alpha >= 1.0
            ? ratingWeight
            : Quantize(alpha * ratingWeight + (1 - alpha) * behaviouralWeight, tuning.WeightQuantum);
    }

    /// <summary>
    /// Diminishing returns toward 1 at <paramref name="saturation"/>. Log rather than linear because
    /// the interesting distinctions are all at the low end — chapter 5 versus chapter 30 says far
    /// more about whether somebody is invested than chapter 300 versus chapter 400 does.
    /// </summary>
    private static double Curve(double value, double saturation)
    {
        if (value <= 0 || saturation <= 0)
        {
            return 0;
        }

        return Math.Clamp(Math.Log(1 + value) / Math.Log(1 + saturation), 0, 1);
    }

    /// <summary>
    /// Exponential decay from 1 (read today) toward <see cref="TasteTuning.RecencyFloor"/>, never
    /// below it. Reading dated in the future — a clock skew, a restored backup — is treated as today
    /// rather than allowed to amplify anything.
    /// </summary>
    private static double Recency(DateTime? lastReadAt, DateOnly today, TasteTuning tuning)
    {
        var floor = Math.Clamp(tuning.RecencyFloor, 0, 1);
        if (lastReadAt is null || tuning.RecencyHalfLifeDays <= 0)
        {
            return floor;
        }

        var days = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(lastReadAt.Value).DayNumber);
        return floor + (1 - floor) * Math.Pow(2, -days / tuning.RecencyHalfLifeDays);
    }

    /// <summary>
    /// Signal (0…1) onto the weight scale, piecewise so that both ends land exactly on
    /// <see cref="TasteTuning.MinWeight"/> and <see cref="TasteTuning.MaxWeight"/> while
    /// <see cref="TasteTuning.NeutralSignal"/> lands exactly on <see cref="Neutral"/>. A single
    /// linear span cannot hit all three, and the one that has to be exact is the middle: it is the
    /// value that means "behave as before".
    /// </summary>
    private static double Map(double signal, TasteTuning tuning)
    {
        var neutral = Math.Clamp(tuning.NeutralSignal, 0, 1);
        signal = Math.Clamp(signal, 0, 1);

        if (signal <= neutral)
        {
            var min = Math.Min(tuning.MinWeight, Neutral);
            return neutral <= 0 ? Neutral : min + (Neutral - min) * (signal / neutral);
        }

        var max = Math.Max(tuning.MaxWeight, Neutral);
        return neutral >= 1 ? Neutral : Neutral + (max - Neutral) * ((signal - neutral) / (1 - neutral));
    }

    /// <summary>
    /// Rounds to a multiple of the quantum. The second round mops up binary representation noise so
    /// two runs that mean the same weight produce the same cache-key text.
    /// </summary>
    private static double Quantize(double weight, double quantum) =>
        quantum <= 0 ? weight : Math.Round(Math.Round(weight / quantum) * quantum, 4);
}
