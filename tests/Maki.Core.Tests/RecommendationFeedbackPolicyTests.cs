using Maki.Core.Entities;
using Maki.Core.Recommendations;

namespace Maki.Core.Tests;

public class RecommendationFeedbackPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Dismissal_expires_at_its_original_boundary()
    {
        var state = new RecommendationFeedback
        {
            Suppression = RecommendationSuppression.Dismissed,
            DismissedUntilUtc = Now.AddDays(30)
        };
        Assert.True(RecommendationFeedbackPolicy.Suppresses(state, Now.AddDays(30).AddTicks(-1)));
        Assert.False(RecommendationFeedbackPolicy.Suppresses(state, Now.AddDays(30)));
    }

    [Fact]
    public void Exposure_and_suppression_are_independent()
    {
        var state = new RecommendationFeedback { Exposure = RecommendationExposure.Anime };
        Assert.True(RecommendationFeedbackPolicy.Suppresses(state, Now));
        state.Exposure = RecommendationExposure.None;
        state.Suppression = RecommendationSuppression.Hidden;
        Assert.True(RecommendationFeedbackPolicy.Suppresses(state, Now));
    }

    [Fact]
    public void Added_weight_is_bounded_and_decays()
    {
        Assert.Equal(1.5, RecommendationFeedbackPolicy.AddedWeight(Now, Now), 8);
        Assert.Equal(1.25, RecommendationFeedbackPolicy.AddedWeight(Now.AddDays(-90), Now), 8);
        Assert.InRange(RecommendationFeedbackPolicy.AddedWeight(Now.AddDays(-900), Now), 1, 1.001);
    }

    [Fact]
    public void Hide_restores_original_dismissal_on_undo_snapshot()
    {
        var state = new RecommendationFeedback();
        Assert.True(RecommendationFeedbackPolicy.Apply(state, "dismiss", RecommendationExposure.None, Now));
        var originalExpiry = state.DismissedUntilUtc;
        Assert.False(RecommendationFeedbackPolicy.Apply(state, "dismiss", RecommendationExposure.None, Now.AddDays(1)));
        Assert.Equal(originalExpiry, state.DismissedUntilUtc);
        Assert.True(RecommendationFeedbackPolicy.Apply(state, "hide", RecommendationExposure.None, Now.AddDays(1)));
        Assert.False(RecommendationFeedbackPolicy.Apply(state, "dismiss", RecommendationExposure.None, Now.AddDays(2)));
        Assert.Equal(RecommendationSuppression.Hidden, state.Suppression);
    }

    [Fact]
    public void Exposure_survives_clearing_suppression_and_media_union()
    {
        var state = new RecommendationFeedback();
        RecommendationFeedbackPolicy.Apply(state, "hide", RecommendationExposure.None, Now);
        RecommendationFeedbackPolicy.Apply(state, "mark-exposed", RecommendationExposure.Manga, Now);
        RecommendationFeedbackPolicy.Apply(state, "mark-exposed", RecommendationExposure.Anime, Now);
        RecommendationFeedbackPolicy.Apply(state, "clear-suppression", RecommendationExposure.None, Now);
        Assert.Equal(RecommendationExposure.Manga | RecommendationExposure.Anime, state.Exposure);
        Assert.True(RecommendationFeedbackPolicy.Suppresses(state, Now));
    }

    [Fact]
    public void Expired_dismissal_can_start_a_new_fixed_window()
    {
        var state = new RecommendationFeedback();
        RecommendationFeedbackPolicy.Apply(state, "dismiss", RecommendationExposure.None, Now);
        Assert.True(RecommendationFeedbackPolicy.Apply(state, "dismiss", RecommendationExposure.None, Now.AddDays(31)));
        Assert.Equal(Now.AddDays(61), state.DismissedUntilUtc);
    }

    [Fact]
    public void A_thumbs_down_hides_the_title_and_a_thumbs_up_takes_the_hide_back()
    {
        var state = new RecommendationFeedback();
        Assert.True(RecommendationFeedbackPolicy.Apply(state, "dislike", RecommendationExposure.None, Now));
        Assert.Equal(RecommendationSentiment.Disliked, state.Sentiment);
        Assert.Equal(RecommendationSuppression.Hidden, state.Suppression);
        Assert.True(RecommendationFeedbackPolicy.Suppresses(state, Now));

        // Liking answers the question hiding was asking, so the two cannot both stand.
        Assert.True(RecommendationFeedbackPolicy.Apply(state, "like", RecommendationExposure.None, Now));
        Assert.Equal(RecommendationSentiment.Liked, state.Sentiment);
        Assert.Equal(RecommendationSuppression.None, state.Suppression);
        Assert.False(RecommendationFeedbackPolicy.Suppresses(state, Now));
    }

    [Fact]
    public void Clearing_a_thumbs_down_leaves_the_title_hidden()
    {
        // One action, one dimension, the same way clearing exposure leaves a hide alone. Putting the
        // title back in the queue is clear-suppression, which the menu offers separately.
        var state = new RecommendationFeedback();
        RecommendationFeedbackPolicy.Apply(state, "dislike", RecommendationExposure.None, Now);
        Assert.True(RecommendationFeedbackPolicy.Apply(state, "clear-sentiment", RecommendationExposure.None, Now));

        Assert.Equal(RecommendationSentiment.None, state.Sentiment);
        Assert.Equal(RecommendationSuppression.Hidden, state.Suppression);
    }

    [Fact]
    public void A_liked_title_outweighs_a_fresh_add_but_not_a_top_rating()
    {
        // Saying so outright beats shelving it; a score somebody sat and chose beats both.
        Assert.True(RecommendationFeedbackPolicy.LikedWeight > RecommendationFeedbackPolicy.AddedWeight(Now, Now));
        Assert.True(RecommendationFeedbackPolicy.LikedWeight < 10 / 5.0);
    }

    [Fact]
    public void Repeating_a_thumbs_up_is_not_a_change()
    {
        var state = new RecommendationFeedback();
        Assert.True(RecommendationFeedbackPolicy.Apply(state, "like", RecommendationExposure.None, Now));
        Assert.False(RecommendationFeedbackPolicy.Apply(state, "like", RecommendationExposure.None, Now));
    }
}
