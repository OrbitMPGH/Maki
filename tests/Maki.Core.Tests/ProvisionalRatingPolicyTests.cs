using Maki.Core.Recommendations;

namespace Maki.Core.Tests;

public class ProvisionalRatingPolicyTests
{
    private static readonly TasteTuning Tuning = TasteTuning.Default;

    [Theory]
    [InlineData(0, 0.35)]
    [InlineData(1, 0.4006)]
    [InlineData(10, 0.5250)]
    [InlineData(60, 0.65)]
    [InlineData(500, 0.65)]
    public void An_ongoing_series_earns_confidence_from_depth_alone(int read, double expected) =>
        Assert.Equal(expected, ProvisionalRatingPolicy.Confidence(read, totalChapters: 40, completed: false), 4);

    [Fact]
    public void Only_reading_most_of_a_finished_series_reaches_the_ceiling()
    {
        Assert.Equal(0.80, ProvisionalRatingPolicy.Confidence(300, totalChapters: 300, completed: true), 4);
        // Chapter lists can run past the dump's total; the ratio is clamped.
        Assert.Equal(0.80, ProvisionalRatingPolicy.Confidence(320, totalChapters: 300, completed: true), 4);
        // The same ten chapters earn about the same on a finished epic as on an ongoing series.
        Assert.Equal(0.53, ProvisionalRatingPolicy.Confidence(10, totalChapters: 300, completed: true), 2);
    }

    [Theory]
    [InlineData(10, 1, 1.40)]
    [InlineData(8, 1, 1.24)]
    [InlineData(6, 1, 1.08)]
    [InlineData(10, 10, 1.52)]
    [InlineData(8, 10, 1.31)]
    [InlineData(6, 10, 1.10)]
    [InlineData(10, 60, 1.65)]
    [InlineData(8, 60, 1.39)]
    [InlineData(6, 60, 1.13)]
    public void Weights_match_the_design_table(int rating, int read, double expected)
    {
        var confidence = ProvisionalRatingPolicy.Confidence(read, null, completed: false);
        Assert.Equal(expected, ProvisionalRatingPolicy.Weight(rating, confidence, Tuning));
    }

    [Fact]
    public void A_provisional_score_never_steers_harder_than_the_same_score_finalised()
    {
        foreach (var completed in new[] { false, true })
        {
            foreach (var read in new[] { 0, 1, 10, 60, 299, 300 })
            {
                var confidence = ProvisionalRatingPolicy.Confidence(read, 300, completed);
                for (var rating = 6; rating <= 10; rating++)
                {
                    Assert.True(ProvisionalRatingPolicy.Weight(rating, confidence, Tuning) < rating / 5.0);
                }

                Assert.Equal(TasteWeights.Neutral, ProvisionalRatingPolicy.Weight(5, confidence, Tuning));
            }
        }
    }

    [Fact]
    public void Avoid_strength_is_the_final_strength_scaled_by_confidence()
    {
        Assert.Equal(0.75 * 0.5, ProvisionalRatingPolicy.AvoidStrength(2, 0.5));
        Assert.Equal(0, ProvisionalRatingPolicy.AvoidStrength(5, 0.8));
    }
}
