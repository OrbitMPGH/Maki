using Maki.Core.Entities;

namespace Maki.Core.Recommendations;

public static class RecommendationFeedbackPolicy
{
    public static bool Apply(RecommendationFeedback state, string action,
        RecommendationExposure medium, DateTime nowUtc)
    {
        var suppression = state.Suppression;
        var expiry = state.DismissedUntilUtc;
        var exposure = state.Exposure;
        var sentiment = state.Sentiment;
        switch (action)
        {
            // Liking clears suppression because the two contradict: a reader who says they liked a
            // title has answered the question hiding it was asking. Exposure is left alone, since
            // "I read this and liked it" is both facts at once.
            case "like":
                state.Sentiment = RecommendationSentiment.Liked;
                state.Suppression = RecommendationSuppression.None;
                state.DismissedUntilUtc = null;
                break;
            // Disliking hides, because continuing to recommend something the reader just rejected is
            // the complaint this exists to answer. It is still only this title: no genre, author or
            // franchise is inferred from it.
            case "dislike":
                state.Sentiment = RecommendationSentiment.Disliked;
                state.Suppression = RecommendationSuppression.Hidden;
                state.DismissedUntilUtc = null;
                break;
            // Only the opinion. Clearing a dislike deliberately leaves the title hidden, the same way
            // clearing exposure leaves a hide alone: one action, one dimension. Restoring it to the
            // queue is clear-suppression, which the menu offers separately.
            case "clear-sentiment":
                state.Sentiment = RecommendationSentiment.None;
                break;
            case "hide":
                state.Suppression = RecommendationSuppression.Hidden;
                state.DismissedUntilUtc = null;
                break;
            case "dismiss" when state.Suppression != RecommendationSuppression.Hidden:
                if (state.Suppression != RecommendationSuppression.Dismissed || state.DismissedUntilUtc is null || state.DismissedUntilUtc <= nowUtc)
                {
                    state.Suppression = RecommendationSuppression.Dismissed;
                    state.DismissedUntilUtc = nowUtc.AddDays(30);
                }
                break;
            case "mark-exposed":
                state.Exposure |= medium == RecommendationExposure.None
                    ? RecommendationExposure.Unspecified : medium;
                break;
            case "clear-suppression":
                state.Suppression = RecommendationSuppression.None;
                state.DismissedUntilUtc = null;
                break;
            case "clear-exposure":
                state.Exposure = RecommendationExposure.None;
                break;
        }
        return state.Suppression != suppression || state.DismissedUntilUtc != expiry ||
            state.Exposure != exposure || state.Sentiment != sentiment;
    }

    public static bool Suppresses(RecommendationFeedback state, DateTime nowUtc) =>
        state.Exposure != RecommendationExposure.None ||
        state.Suppression == RecommendationSuppression.Hidden ||
        state.Suppression == RecommendationSuppression.Dismissed && state.DismissedUntilUtc > nowUtc;

    public static double AddedWeight(DateTime addedAtUtc, DateTime nowUtc) =>
        1 + 0.5 * Math.Pow(2, -Math.Max(0, (nowUtc - addedAtUtc).TotalDays) / 90);

    /// <summary>
    /// Seed weight for a liked title, on the same scale as a rating's <c>rating / 5.0</c>.
    /// <para>
    /// Deliberately below the 2.0 a explicit 10 carries: a thumbs up is a strong signal but a less
    /// precise one than a score somebody sat and chose, and a reader who does both should not find
    /// the cheaper action outranking the considered one. Above the 1.5 ceiling a brand new personal
    /// add can reach, because saying so outright is better evidence than shelving it.
    /// </para>
    /// </summary>
    public const double LikedWeight = 1.6;

    /// <summary>
    /// The highest rating that still means "less of this". 1 through 4 join the avoided set; 5 and
    /// above stay positive seeds.
    /// <para>
    /// Four and not five because 5 is the neutral point of the <c>rating / 5.0</c> scale the seed
    /// weights are built on: it maps to exactly 1.0, which is what an unrated title already gets.
    /// Reading neutral as a complaint would turn "I have no strong feeling" into a penalty on
    /// everything that resembles it, and a reader who wanted that had 1 through 4 to say it with.
    /// </para>
    /// </summary>
    public const int AvoidRatingCeiling = 4;

    /// <summary>
    /// How hard a low rating pushes similar titles down, on (0, 1]: 1 → 1.0, 2 → 0.75, 3 → 0.5,
    /// 4 → 0.25. Zero for any rating at or above the neutral point, which carries no avoidance.
    /// </summary>
    public static double AvoidStrength(int rating) =>
        rating is >= 1 and <= AvoidRatingCeiling ? (5 - rating) / 4.0 : 0;
}
