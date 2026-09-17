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
        switch (action)
        {
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
            state.Exposure != exposure;
    }

    public static bool Suppresses(RecommendationFeedback state, DateTime nowUtc) =>
        state.Exposure != RecommendationExposure.None ||
        state.Suppression == RecommendationSuppression.Hidden ||
        state.Suppression == RecommendationSuppression.Dismissed && state.DismissedUntilUtc > nowUtc;

    public static double AddedWeight(DateTime addedAtUtc, DateTime nowUtc) =>
        1 + 0.5 * Math.Pow(2, -Math.Max(0, (nowUtc - addedAtUtc).TotalDays) / 90);
}
