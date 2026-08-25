using Maki.Core.Recommendations;

namespace Maki.Core.Tests;

/// <summary>
/// The behavioural seed weight. Most of these pin properties that are easy to lose while tuning the
/// curves: the scale has to stay commensurable with <c>rating / 5.0</c>, the output has to stay
/// coarse enough not to churn the recommendation pool cache, and missing time data must never read
/// as an absence of engagement.
/// </summary>
public class TasteWeightsTests
{
    private static readonly DateOnly Today = new(2026, 8, 24);
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TasteTuning Tuning = TasteTuning.Default;

    private static double Weight(
        int completed, int downloaded, long seconds, DateTime? lastReadAt, TasteTuning? tuning = null) =>
        TasteWeights.Weight(
            new SeriesReadSignal(completed, downloaded, seconds, lastReadAt), Today, tuning ?? Tuning);

    [Fact]
    public void Unread_series_is_neutral()
    {
        Assert.Equal(TasteWeights.Neutral, Weight(0, 40, 0, null));
    }

    [Fact]
    public void Uniform_tuning_weights_everything_neutrally()
    {
        Assert.Equal(TasteWeights.Neutral, Weight(200, 200, 100_000, Now, TasteTuning.Uniform));
    }

    [Fact]
    public void Finished_series_read_today_hits_the_ceiling()
    {
        Assert.Equal(Tuning.MaxWeight, Weight(80, 80, 20_000, Now), 3);
    }

    [Fact]
    public void Weight_stays_inside_the_configured_band()
    {
        int[] chapters = [1, 2, 5, 30, 400];
        int[] libraries = [1, 5, 50, 400];
        long[] times = [0, 30, 600, 500_000];
        DateTime?[] dates = [null, Now, Now.AddDays(-30), Now.AddYears(-8)];

        foreach (var completed in chapters)
        foreach (var downloaded in libraries)
        foreach (var seconds in times)
        foreach (var date in dates)
        {
            var weight = Weight(completed, downloaded, seconds, date);
            Assert.InRange(weight, Tuning.MinWeight, Tuning.MaxWeight);
        }
    }

    [Fact]
    public void Weight_is_a_multiple_of_the_quantum()
    {
        // Load-bearing: these values are rendered into RecommendationService's pool cache key, so a
        // weight that drifts below the quantum would invalidate a 12-hour pool on every chapter read.
        foreach (var completed in Enumerable.Range(1, 60))
        {
            var steps = Weight(completed, 60, completed * 137L, Now.AddDays(-completed)) / Tuning.WeightQuantum;
            Assert.Equal(Math.Round(steps), steps, 6);
        }
    }

    [Fact]
    public void Materially_different_histories_do_not_collapse_to_the_same_weight()
    {
        // The quantum is not free. SemanticRecommender.PickRepresentativeSeeds orders seeds by weight
        // and breaks ties with ThenBy(id), and the top seed starts the farthest-point walk that decides
        // the whole pool — so a quantum coarse enough to tie two genuinely different histories hands
        // that decision to an arbitrary catalogue id. These two are the real pair that exposed it
        // (62/74 chapters versus 41/43, both read a month ago), which tied at the original 0.1.
        var thorough = Weight(41, 43, 0, Now.AddDays(-30));
        var broader = Weight(62, 74, 0, Now.AddDays(-30));

        Assert.True(
            thorough > broader,
            $"41/43 ({thorough}) must outrank 62/74 ({broader}) rather than tie and lose on id");
    }

    [Fact]
    public void The_quantum_stays_finer_than_the_cache_key_it_serves()
    {
        // RecommendationService renders weights into the pool cache key at one decimal. The quantum
        // exists to snap float noise, not to do that coarsening, and a quantum at or above the key's
        // resolution starts deciding seed order instead.
        Assert.True(Tuning.WeightQuantum <= 0.05);
    }

    [Fact]
    public void More_chapters_finished_never_lowers_the_weight()
    {
        var previous = 0.0;
        foreach (var completed in Enumerable.Range(1, 120))
        {
            var weight = Weight(completed, 120, 0, Now);
            Assert.True(weight >= previous, $"{completed} chapters dropped below {completed - 1}");
            previous = weight;
        }

        Assert.True(Weight(120, 120, 0, Now) > Weight(1, 120, 0, Now));
    }

    [Fact]
    public void More_time_never_lowers_the_weight()
    {
        var previous = 0.0;
        foreach (var minutes in new[] { 1, 5, 20, 60, 240, 1200 })
        {
            var weight = Weight(20, 40, minutes * 60L, Now);
            Assert.True(weight >= previous, $"{minutes} minutes dropped below the previous step");
            previous = weight;
        }
    }

    [Fact]
    public void Older_reading_never_raises_the_weight()
    {
        var previous = double.MaxValue;
        foreach (var days in new[] { 0, 30, 120, 365, 1500, 5000 })
        {
            var weight = Weight(40, 40, 6000, Now.AddDays(-days));
            Assert.True(weight <= previous, $"{days} days ago outranked something more recent");
            previous = weight;
        }
    }

    [Fact]
    public void Recency_decays_to_the_floor_and_no_further()
    {
        var decade = Weight(40, 40, 6000, Now.AddYears(-10));
        var century = Weight(40, 40, 6000, Now.AddYears(-100));
        Assert.Equal(decade, century, 3);

        // The floor sits above NeutralSignal on purpose: an old favourite is still evidence, and must
        // not decay into the same weight as a series that was never opened.
        Assert.True(decade > TasteWeights.Neutral);
    }

    [Fact]
    public void Missing_time_is_unknown_rather_than_zero_engagement()
    {
        // The Kavita import case. Those rows carry no ReadSeconds at all, so scoring them as no
        // engagement would rank a whole imported back catalogue below a series somebody opened twice.
        var imported = Weight(50, 50, 0, Now);
        var dipped = Weight(2, 50, 300, Now);

        Assert.True(imported > dipped, $"imported finish {imported} did not outrank a two-chapter dip {dipped}");
        Assert.Equal(Tuning.MaxWeight, imported, 3);
    }

    [Fact]
    public void Missing_time_scores_the_same_as_time_that_matches_the_other_channels()
    {
        // Renormalizing (rather than substituting a value) means dropping the channel cannot move the
        // weight on its own — a series with no time data scores exactly what the remaining evidence says.
        var noTime = Weight(12, 40, 0, Now);
        var withAverageTime = TasteWeights.Weight(
            new SeriesReadSignal(12, 40, 0, Now), Today, Tuning with { EngageWeight = 0 });

        Assert.Equal(withAverageTime, noTime, 6);
    }

    [Fact]
    public void Deep_reading_of_a_long_series_outranks_a_finished_one_shot()
    {
        var epic = Weight(180, 400, 90_000, Now);
        var oneShot = Weight(1, 1, 400, Now);

        Assert.True(epic > oneShot, $"180 chapters in ({epic}) lost to a finished one-shot ({oneShot})");
    }

    [Fact]
    public void Blend_returns_the_rating_untouched_at_alpha_one()
    {
        foreach (var rating in Enumerable.Range(1, 10).Select(r => r / 5.0))
        {
            Assert.Equal(rating, TasteWeights.Blend(rating, 0.4, Tuning));
        }
    }

    [Fact]
    public void Blend_moves_toward_behaviour_as_alpha_falls()
    {
        var half = TasteWeights.Blend(2.0, 0.4, Tuning with { RatingBlendAlpha = 0.5 });
        Assert.Equal(1.2, half, 6);

        Assert.Equal(0.4, TasteWeights.Blend(2.0, 0.4, Tuning with { RatingBlendAlpha = 0 }), 6);
    }

    [Fact]
    public void Derived_weight_never_outranks_a_top_rating()
    {
        // The blend rule already gives an explicit rating the last word; keeping the ceiling below
        // 10/10's 2.0 means it wins on the scale too, so a partial blend can never invert them.
        Assert.True(Tuning.MaxWeight < 10 / 5.0);
        Assert.True(Tuning.MinWeight > 1 / 5.0);
    }
}
