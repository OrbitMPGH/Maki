using Maki.Core.Entities;

namespace Maki.Core.Recommendations;

public enum AnimeSignalRole { Neutral, Positive, Avoided }

/// <summary>
/// What a watched anime says about the manga it was adapted from, on the same scales the library
/// and the feedback rows already use.
/// <para>
/// Everything here is deliberately weaker than its library equivalent. A reader who finished an
/// anime has not read the manga, and the two diverge often enough - pacing, an anime-original
/// ending, a different arc - that treating a 9/10 anime as a 9/10 manga would put a stranger's
/// opinion in the reader's profile at full strength. <see cref="SeedScale"/> is the whole of that
/// discount, in one place, so it can be measured later without hunting for the arithmetic.
/// </para>
/// </summary>
public static class AnimeSignalPolicy
{
    /// <summary>
    /// How much of a rating's own seed weight an anime-derived seed keeps. A 10/10 anime lands at
    /// 0.7 against the 2.0 a 10/10 manga carries, which is below the neutral 1.0 an unrated shelf
    /// title gets: present in the profile, never steering it.
    /// </summary>
    public const double SeedScale = 0.7;

    /// <summary>What an unscored but completed anime stands in at, on the 0-1 pre-scale range.</summary>
    public const double UnscoredCredit = 0.8;

    /// <summary>The lowest score that still reads as enthusiasm. 5 and 6 are neutral and ignored.</summary>
    public const int PositiveScoreFloor = 7;

    /// <summary>
    /// The highest score that still means "less of this", on the 0-10 scale both trackers report.
    /// <para>
    /// The same number as <see cref="RecommendationFeedbackPolicy.AvoidRatingCeiling"/> and for the
    /// same reason, but it is deliberately its own constant: that one is a ceiling on the 1-5 star
    /// ratings a library row carries, and borrowing its <c>AvoidStrength</c> curve here would be
    /// reading a 4/10 as a 4/5. They agree today; nothing makes them have to.
    /// </para>
    /// </summary>
    public const int AvoidScoreCeiling = 4;

    /// <summary>
    /// What a drop with no score pushes with. Below the 1.0 a thumbs down carries: abandoning a
    /// show is a weaker statement than rejecting a recommendation outright, and a lot of them are
    /// about time rather than taste.
    /// </summary>
    public const double DroppedStrength = 0.75;

    public static AnimeSignalRole RoleOf(AnimeWatchStatus status, int? score)
    {
        if (status == AnimeWatchStatus.Dropped || AvoidStrengthOf(status, score) > 0)
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
    /// How hard a score at or below <see cref="AvoidScoreCeiling"/> pushes, on (0, 1]: 1 -> 1.0,
    /// 2 -> 0.75, 3 -> 0.5, 4 -> 0.25. Zero for 5 and above, which carry no complaint.
    /// <para>
    /// Its own curve over the 0-10 scale rather than a call into the star-rating one. Passing a
    /// 10-point score to a function whose domain is 1-5 reads a 6/10 as "off the scale, no
    /// avoidance" by luck rather than by intent, and would have silently inverted if either
    /// ceiling moved.
    /// </para>
    /// </summary>
    public static double AvoidStrengthOfScore(int score) =>
        score is >= 1 and <= AvoidScoreCeiling
            ? (AvoidScoreCeiling + 1 - score) / (double)AvoidScoreCeiling
            : 0;

    /// <summary>
    /// The avoidance strength in (0, 1], or 0 for anything that is not a complaint. A drop adds its
    /// own floor on top of whatever the score said.
    /// </summary>
    public static double AvoidStrengthOf(AnimeWatchStatus status, int? score)
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

    /// <summary>The positive seed weight, or 0 when the entry is not a positive one.</summary>
    public static double SeedWeightOf(AnimeWatchStatus status, int? score) =>
        RoleOf(status, score) != AnimeSignalRole.Positive
            ? 0
            : (score is { } s ? s / 10.0 : UnscoredCredit) * SeedScale;
}
