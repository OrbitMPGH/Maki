using Maki.Core.Entities;

namespace Maki.Core.Recommendations;

public enum AnimeSignalRole { Neutral, Positive, Avoided }

/// <summary>
/// How much authority a reader lets their watch history carry over their manga recommendations.
/// <para>
/// One dial rather than a pair, because the two halves have to move together. A reader who does not
/// trust their anime scores does not trust the low ones either, and a level that discounted the
/// praise while leaving the complaints at full strength would quietly make the feature a net
/// negative - which is what shipped before this existed.
/// </para>
/// </summary>
public enum AnimeSignalStrength
{
    /// <summary>Barely there. What the feature did before the dial existed, on the positive side.</summary>
    Subtle,

    /// <summary>Half of what a manga rating carries. The default; see <see cref="AnimeSignalPolicy.RatingShareOf"/>.</summary>
    Balanced,

    /// <summary>A watched anime counts exactly as much as having rated the manga itself.</summary>
    Full,
}

/// <summary>Why a matched anime signal reaches nothing, or <see cref="None"/> when it does.</summary>
public enum AnimeSupersededBy
{
    None,

    /// <summary>The manga is on the reader's shelf, rated or not.</summary>
    Library,

    /// <summary>They thumbed the manga up or down themselves.</summary>
    Feedback,

    /// <summary>They asked the recommender to stop steering by this title.</summary>
    Ignored,
}

/// <summary>
/// Which of a reader's own evidence outranks their watch history, in one place.
/// <para>
/// This is the whole of "do not count the same series twice". An anime signal is second-hand
/// evidence about a book, so anything first-hand replaces it outright rather than adding to it: a
/// reader who rated the manga 9 and the adaptation 10 has one opinion about the book, and a reader
/// who rated the manga 9 and dropped the adaptation has not complained about it at all.
/// </para>
/// <para>
/// Owning it is enough on its own, rated or not, because a shelf row carries reading history and
/// that is better evidence about the book than an adaptation of it. Defined here rather than inline
/// in the seed builder because the panel has to describe the same rule it applies: an entry the
/// recommender quietly skips while the screen calls it "positive" is how somebody concludes the
/// feature is double counting.
/// </para>
/// </summary>
public readonly record struct AnimeSignalPrecedence(
    IReadOnlySet<long> Library, IReadOnlySet<long> Opinion, IReadOnlySet<long> Ignored)
{
    public AnimeSupersededBy Reason(long mangaBakaId) =>
        Library.Contains(mangaBakaId) ? AnimeSupersededBy.Library
        : Opinion.Contains(mangaBakaId) ? AnimeSupersededBy.Feedback
        : Ignored.Contains(mangaBakaId) ? AnimeSupersededBy.Ignored
        : AnimeSupersededBy.None;

    public bool Supersedes(long mangaBakaId) => Reason(mangaBakaId) != AnimeSupersededBy.None;
}

/// <summary>
/// What a watched anime says about the manga it was adapted from, on the same scales the library
/// and the feedback rows already use.
/// <para>
/// Everything here is weaker than its library equivalent by default, and
/// <see cref="RatingShareOf"/> is the whole of that discount in one number, so it can be measured
/// later without hunting for the arithmetic. A reader who finished an anime has not read the manga,
/// and the two diverge often enough - pacing, an anime-original ending, a different arc, a studio
/// that could not draw it - that a 9/10 anime is evidence about an adaptation first and about the
/// book second.
/// </para>
/// <para>
/// The scores reaching every method here are <see cref="AnimeSignalGrouping"/> averages, so they are
/// routinely fractional: a reader who gave season 1 a 9 and season 2 a 6 means 7.5, not 7 or 8.
/// </para>
/// </summary>
public static class AnimeSignalPolicy
{
    public const AnimeSignalStrength DefaultStrength = AnimeSignalStrength.Balanced;

    /// <summary>
    /// How much of a manga rating's authority one level carries, which is the only number that
    /// separates the three. Everything else on this class is the same arithmetic scaled by it.
    /// <para>
    /// The library scale is the anchor: a rated series seeds at <c>rating / 5.0</c>, so a 10 carries
    /// 2.0 and a 5 carries the neutral 1.0, and a low rating avoids on a curve topping out at 1.0.
    /// A share of 1.0 here reproduces both exactly.
    /// </para>
    /// <list type="bullet">
    /// <item><b>Subtle (0.35)</b> is what the feature shipped as: a 10/10 anime lands at 0.7, under
    /// the neutral 1.0 an unrated shelf title already gets, so it is present in the profile and
    /// cannot steer it.</item>
    /// <item><b>Balanced (0.5)</b> is half a rating, and the default. A 10/10 anime lands at exactly
    /// 1.0 - neutral - which is the useful place for it: the title still contributes its genres and
    /// its tags to the profile the recommender builds, and still cannot out-vote a book the reader
    /// actually rated. An adaptation that butchered the art or rewrote the ending drags the score
    /// down without dragging the source's themes out of the profile with it.</item>
    /// <item><b>Full (1.0)</b> treats watching as reading. For a reader whose anime scores really do
    /// track the manga - or who only watches adaptations they already trust.</item>
    /// </list>
    /// </summary>
    public static double RatingShareOf(AnimeSignalStrength strength) => strength switch
    {
        AnimeSignalStrength.Subtle => 0.35,
        AnimeSignalStrength.Full => 1.0,
        _ => 0.5,
    };

    /// <summary>
    /// What a full share is worth against a score on the trackers' 0-10 scale. The library seeds on
    /// <c>rating / 5.0</c> and this scores on <c>score / 10.0</c>, so the two agree at 2.0.
    /// </summary>
    private const double RatingSeedScale = 2.0;

    /// <summary>The seed scale at one level: <c>Subtle</c> keeps the 0.7 this shipped with.</summary>
    public static double SeedScaleOf(AnimeSignalStrength strength) =>
        RatingSeedScale * RatingShareOf(strength);

    /// <summary>
    /// What an unscored but completed anime stands in at, on the 0-1 pre-scale range.
    /// <see cref="AnimeSignalSyncService"/> stores scored entries only, so this branch is reachable
    /// today only for rows a sync wrote before that change; nothing new lands here.
    /// </summary>
    public const double UnscoredCredit = 0.8;

    /// <summary>The lowest score that still reads as enthusiasm. 5 and 6 are neutral and ignored.</summary>
    public const int PositiveScoreFloor = 7;

    /// <summary>
    /// The highest score that still means "less of this", on the 0-10 scale both trackers report.
    /// <para>
    /// The same number as <see cref="RecommendationFeedbackPolicy.AvoidRatingCeiling"/> and for the
    /// same reason, but it is deliberately its own constant: that one is a ceiling on the ratings a
    /// library row carries, and borrowing its <c>AvoidStrength</c> curve here would be reading a
    /// 4/10 as a 4/5. They agree today; nothing makes them have to.
    /// </para>
    /// </summary>
    public const int AvoidScoreCeiling = 4;

    /// <summary>
    /// What a drop with no score pushes with at a full share, before the level scales it. Below the
    /// 1.0 a thumbs down carries: abandoning a show is a weaker statement than rejecting a
    /// recommendation outright, and a lot of them are about time rather than taste.
    /// </summary>
    public const double DroppedStrength = 0.75;

    /// <summary>
    /// Which way a signal points, which the level deliberately does not change. A 9/10 anime is
    /// enthusiasm whether the reader trusts it at 0.35 or at 1.0, and a panel whose badges flipped
    /// between "positive" and "neutral" as somebody dragged a dial would be describing the dial
    /// rather than their watch history. The level scales how hard it pushes, never whether it does.
    /// </summary>
    public static AnimeSignalRole RoleOf(AnimeWatchStatus status, double? score)
    {
        if (status == AnimeWatchStatus.Dropped || RawAvoidStrengthOf(status, score) > 0)
        {
            return AnimeSignalRole.Avoided;
        }

        if (status == AnimeWatchStatus.Planning)
        {
            return AnimeSignalRole.Neutral;
        }

        return score is null
            ? status == AnimeWatchStatus.Completed ? AnimeSignalRole.Positive : AnimeSignalRole.Neutral
            : score >= PositiveScoreFloor ? AnimeSignalRole.Positive : AnimeSignalRole.Neutral;
    }

    /// <summary>
    /// How hard a score at or below <see cref="AvoidScoreCeiling"/> pushes at a full share, on
    /// (0, 1]: 1 -> 1.0, 2 -> 0.75, 3 -> 0.5, 4 -> 0.25. Zero for 5 and above, which carry no
    /// complaint, and the curve is continuous in between so an averaged 3.5 lands at 0.375 rather
    /// than on a step.
    /// <para>
    /// Its own curve over the 0-10 scale rather than a call into the star-rating one. Passing a
    /// 10-point score to a function whose domain is 1-5 reads a 6/10 as "off the scale, no
    /// avoidance" by luck rather than by intent, and would have silently inverted if either
    /// ceiling moved.
    /// </para>
    /// </summary>
    public static double AvoidStrengthOfScore(double score) =>
        score is >= 1 and <= AvoidScoreCeiling
            ? (AvoidScoreCeiling + 1 - score) / AvoidScoreCeiling
            : 0;

    /// <summary>
    /// The avoidance strength in (0, 1] at a full share, or 0 for anything that is not a complaint.
    /// A drop adds its own floor on top of whatever the score said.
    /// </summary>
    private static double RawAvoidStrengthOf(AnimeWatchStatus status, double? score)
    {
        // Planning is not evidence either way: nobody drops a show they never started, and a score
        // on an unwatched entry is somebody rating the premise.
        if (status == AnimeWatchStatus.Planning)
        {
            return 0;
        }

        var rated = score is { } s ? AvoidStrengthOfScore(s) : 0;
        return status == AnimeWatchStatus.Dropped ? Math.Max(rated, DroppedStrength) : rated;
    }

    /// <summary>
    /// The avoidance strength the recommender subtracts with, after the level.
    /// <para>
    /// This half is what the dial is really for. A low anime score is the least trustworthy thing a
    /// watch history can say about a book: people rate an adaptation badly for the adaptation - a
    /// rewritten ending, a rushed cour, art that lost what the panels had - and none of that is a
    /// complaint about the manga. Before the dial, a 1/10 anime pushed at the full 1.0 a deliberate
    /// thumbs down carries, so the one direction most likely to be wrong was also the loudest.
    /// </para>
    /// </summary>
    public static double AvoidStrengthOf(
        AnimeWatchStatus status, double? score, AnimeSignalStrength strength) =>
        RawAvoidStrengthOf(status, score) * RatingShareOf(strength);

    /// <summary>The positive seed weight, or 0 when the entry is not a positive one.</summary>
    public static double SeedWeightOf(
        AnimeWatchStatus status, double? score, AnimeSignalStrength strength) =>
        RoleOf(status, score) != AnimeSignalRole.Positive
            ? 0
            : (score is { } s ? s / 10.0 : UnscoredCredit) * SeedScaleOf(strength);

    /// <summary>Round-trips the stored setting value. Anything unrecognised falls back to the default.</summary>
    public static AnimeSignalStrength ParseStrength(string? raw) => raw?.ToLowerInvariant() switch
    {
        "subtle" => AnimeSignalStrength.Subtle,
        "full" => AnimeSignalStrength.Full,
        _ => DefaultStrength,
    };

    public static string NameOf(AnimeSignalStrength strength) => strength switch
    {
        AnimeSignalStrength.Subtle => "subtle",
        AnimeSignalStrength.Full => "full",
        _ => "balanced",
    };
}
