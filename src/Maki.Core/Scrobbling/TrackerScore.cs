using Maki.Core.Ratings;

namespace Maki.Core.Scrobbling;

/// <summary>
/// Maps Maki's 1-10 rating (one decimal) to each tracker's own scale. 0 always means "clear".
/// </summary>
public static class TrackerScore
{
    /// <summary>
    /// A 0-100 score in whole points, which keeps the decimal (7.8 is 78): AniList's <c>scoreRaw</c>,
    /// whatever the user's display format, and MangaBaka's library rating.
    /// </summary>
    public static int Hundred(double score) =>
        Math.Clamp((int)Math.Round(score * 10, MidpointRounding.AwayFromZero), 0, 100);

    /// <summary>
    /// A 0-100 score read back onto Maki's 1-10 scale. Below 10 is null: under Maki's lowest rating,
    /// and what older Maki versions wrote to MangaBaka when they sent 0-10 there (an 8 stored as 8 of 100).
    /// </summary>
    public static double? FromHundred(double raw) =>
        raw >= 10 ? RatingScale.Round(Math.Min(raw / 10, 10)) : null;

    /// <summary>A 1-10 score to one decimal, for trackers that store any number (MangaUpdates).</summary>
    public static double Tenths(double score) => RatingScale.Round(Math.Clamp(score, 1, 10));

    /// <summary>Kitsu's <c>ratingTwenty</c> (2-20 in half points), or null to clear.</summary>
    public static int? KitsuTwenty(double score) =>
        score <= 0 ? null : Math.Clamp((int)Math.Round(score * 2, MidpointRounding.AwayFromZero), 2, 20);

    /// <summary>A whole 0-10 score, for trackers that store integers.</summary>
    public static int Whole(double score) =>
        Math.Clamp((int)Math.Round(score, MidpointRounding.AwayFromZero), 0, 10);

    /// <summary>
    /// <paramref name="rating"/> as a tracker with resolution <paramref name="step"/> would hold it, so
    /// a local 7.8 and a MAL 8 count as the same score.
    /// </summary>
    public static double ToStep(double rating, double step) =>
        step <= 0 ? rating : RatingScale.Round(Math.Round(rating / step, MidpointRounding.AwayFromZero) * step);

    /// <summary>
    /// Whether a tracker's score is a real change from the local one, judged at the tracker's own
    /// resolution: a local 7.8 against a MAL 8 is not, against an AniList 8.0 it is.
    /// </summary>
    public static bool Differs(double? local, double remote, double step) =>
        local is not { } rating || Math.Abs(ToStep(rating, step) - RatingScale.Round(remote)) > 0.001;
}
