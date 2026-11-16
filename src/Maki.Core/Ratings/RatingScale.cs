namespace Maki.Core.Ratings;

/// <summary>
/// A reader's rating: 1-10 with at most one decimal. Kept on the 1-10 scale so thresholds such as
/// "4 or under" keep their meaning.
/// </summary>
public static class RatingScale
{
    public const double Min = 1;
    public const double Max = 10;

    public static bool InRange(double rating) => rating is >= Min and <= Max;

    /// <summary>The value every write stores: one decimal, half away from zero.</summary>
    public static double Round(double rating) => Math.Round(rating, 1, MidpointRounding.AwayFromZero);
}
