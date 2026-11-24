using System.Text.Json;
using Maki.Core.Ratings;
using Maki.Core.Recommendations;
using Maki.Core.Scrobbling;

namespace Maki.Core.Tests;

/// <summary>
/// The rating breakdown category list, breakdown validation, and how a one-decimal rating maps to
/// each tracker's scale.
/// </summary>
public class RatingScoresTests
{
    private static RatingCategory Custom(string key, string? name = "Music", bool enabled = false, double weight = 1) =>
        new(key, name, enabled, weight);

    [Fact]
    public void Unset_or_unreadable_categories_are_the_defaults()
    {
        Assert.Equal(RatingCategoryDefaults.Defaults, RatingCategoryDefaults.Parse(null));
        Assert.Equal(RatingCategoryDefaults.Defaults, RatingCategoryDefaults.Parse("not json"));
        Assert.Equal(RatingCategoryDefaults.Defaults, RatingCategoryDefaults.Parse("""[{"key":"nope","enabled":true,"weight":1}]"""));
    }

    [Fact]
    public void Defaults_enable_the_first_four_built_ins_at_weight_one()
    {
        var defaults = RatingCategoryDefaults.Defaults;

        Assert.Equal(["story", "characters", "art", "enjoyment", "pacing", "world", "originality", "ending"],
            defaults.Select(c => c.Key));
        Assert.Equal([true, true, true, true, false, false, false, false], defaults.Select(c => c.Enabled));
        Assert.All(defaults, c => Assert.Equal(1, c.Weight));
        Assert.All(defaults, c => Assert.Null(c.Name));
    }

    [Fact]
    public void Order_is_kept_and_missing_built_ins_are_appended()
    {
        var normalized = RatingCategoryDefaults.Normalize(
        [
            new RatingCategory("ending", null, true, 2),
            Custom("c-music", "  Music ", true, 0.5),
            new RatingCategory("story", "Plot", false, 1),
        ])!;

        Assert.Equal(["ending", "c-music", "story", "characters", "art", "enjoyment", "pacing", "world", "originality"],
            normalized.Select(c => c.Key));
        Assert.Equal("Music", normalized[1].Name);
        Assert.Null(normalized[2].Name);
        Assert.False(normalized[2].Enabled);
    }

    [Fact]
    public void Saved_categories_round_trip()
    {
        var saved = RatingCategoryDefaults.Normalize([Custom("c-music", enabled: true), new RatingCategory("art", null, true, 2)])!;

        Assert.Equal(saved, RatingCategoryDefaults.Parse(RatingCategoryDefaults.Serialize(saved)));
    }

    [Theory]
    [InlineData("plot")]
    [InlineData("c-")]
    [InlineData("c-Music")]
    [InlineData("c-music-box")]
    [InlineData("c-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Unknown_or_malformed_keys_are_refused(string key) =>
        Assert.Null(RatingCategoryDefaults.Normalize([Custom(key)]));

    [Fact]
    public void A_37_character_custom_key_is_accepted() =>
        Assert.NotNull(RatingCategoryDefaults.Normalize([Custom("c-" + new string('a', 37))]));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void A_custom_category_needs_a_name_of_up_to_forty_characters(string? name) =>
        Assert.Null(RatingCategoryDefaults.Normalize([Custom("c-music", name)]));

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(3)]
    public void Weights_other_than_half_one_and_two_are_refused(double weight) =>
        Assert.Null(RatingCategoryDefaults.Normalize([new RatingCategory("story", null, true, weight)]));

    [Fact]
    public void Duplicate_keys_are_refused() =>
        Assert.Null(RatingCategoryDefaults.Normalize(
            [new RatingCategory("story", null, true, 1), new RatingCategory("story", null, false, 1)]));

    [Fact]
    public void At_most_eight_enabled()
    {
        var builtIns = RatingCategoryDefaults.BuiltInKeys.Select(k => new RatingCategory(k, null, true, 1)).ToList();

        Assert.NotNull(RatingCategoryDefaults.Normalize(builtIns));
        Assert.Null(RatingCategoryDefaults.Normalize([.. builtIns, Custom("c-music", enabled: true)]));
    }

    [Fact]
    public void At_most_eight_custom()
    {
        var custom = Enumerable.Range(0, 8).Select(i => Custom($"c-x{i}")).ToList();

        Assert.NotNull(RatingCategoryDefaults.Normalize(custom));
        Assert.Null(RatingCategoryDefaults.Normalize([.. custom, Custom("c-x8")]));
    }

    [Fact]
    public void An_appended_built_in_stays_off_when_eight_are_already_enabled()
    {
        var normalized = RatingCategoryDefaults.Normalize(
            Enumerable.Range(0, 8).Select(i => Custom($"c-x{i}", enabled: true)).ToList())!;

        Assert.Equal(8, normalized.Count(c => c.Enabled));
        Assert.All(normalized.Where(c => RatingCategoryDefaults.IsBuiltIn(c.Key)), c => Assert.False(c.Enabled));
    }

    private static IReadOnlyDictionary<string, JsonElement> Scores(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void A_valid_breakdown_keeps_its_order()
    {
        var scores = RatingBreakdown.Validate(Scores("""{"story":8,"c-k3j9a2":7,"art":6}"""))!;

        Assert.Equal(["story", "c-k3j9a2", "art"], scores.Keys);
        Assert.Equal("""{"story":8,"c-k3j9a2":7,"art":6}""", RatingBreakdown.Serialize(scores));
    }

    [Fact]
    public void An_empty_breakdown_serializes_to_nothing() =>
        Assert.Null(RatingBreakdown.Serialize(RatingBreakdown.Validate(Scores("{}"))!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("{\"story\":")]
    public void An_unset_or_unreadable_breakdown_parses_to_null(string? json) =>
        Assert.Null(RatingBreakdown.Parse(json));

    [Theory]
    [InlineData(7.8, 78)]
    [InlineData(10, 100)]
    [InlineData(1, 10)]
    [InlineData(0, 0)]
    public void AniList_takes_a_hundred_point_score(double score, int raw) =>
        Assert.Equal(raw, TrackerScore.Hundred(score));

    [Theory]
    [InlineData(7.8, 16)]
    [InlineData(7.7, 15)]
    [InlineData(7.5, 15)]
    [InlineData(1, 2)]
    [InlineData(10, 20)]
    public void Kitsu_takes_half_points_on_twenty(double score, int twenty) =>
        Assert.Equal(twenty, TrackerScore.KitsuTwenty(score));

    [Fact]
    public void Kitsu_clears_on_zero() => Assert.Null(TrackerScore.KitsuTwenty(0));

    [Theory]
    [InlineData(78, 7.8)]
    [InlineData(80, 8.0)]
    [InlineData(10, 1.0)]
    [InlineData(100, 10.0)]
    public void A_hundred_point_score_reads_back_to_one_decimal(double raw, double score) =>
        Assert.Equal(score, TrackerScore.FromHundred(raw));

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9.5)]
    public void A_hundred_point_score_under_ten_is_unrated(double raw) => Assert.Null(TrackerScore.FromHundred(raw));

    [Theory]
    [InlineData(7.84, 7.8)]
    [InlineData(7.86, 7.9)]
    [InlineData(0.3, 1.0)]
    [InlineData(10, 10.0)]
    public void Decimal_trackers_get_one_decimal_within_one_to_ten(double score, double sent) =>
        Assert.Equal(sent, TrackerScore.Tenths(score));

    [Theory]
    [InlineData(7.5, 8)]
    [InlineData(7.4, 7)]
    [InlineData(7.8, 8)]
    [InlineData(10, 10)]
    [InlineData(0, 0)]
    public void Whole_number_trackers_round_half_away_from_zero(double score, int whole) =>
        Assert.Equal(whole, TrackerScore.Whole(score));

    [Theory]
    [InlineData(7.8, 8, 1, false)]
    [InlineData(7.8, 7, 1, true)]
    [InlineData(7.8, 7.8, 0.1, false)]
    [InlineData(7.8, 8.0, 0.1, true)]
    [InlineData(7.8, 8.0, 0.5, false)]
    [InlineData(7.6, 7.5, 0.5, false)]
    [InlineData(7.6, 7.0, 0.5, true)]
    [InlineData(null, 7, 1, true)]
    public void An_import_lists_only_scores_that_differ_at_the_trackers_resolution(
        double? local, double remote, double step, bool differs) =>
        Assert.Equal(differs, TrackerScore.Differs(local, remote, step));

    [Theory]
    [InlineData(4.0, 0.25)]
    [InlineData(4.1, 0)]
    [InlineData(3.5, 0.375)]
    [InlineData(5, 0)]
    public void Four_point_zero_is_the_last_avoided_rating(double rating, double strength) =>
        Assert.Equal(strength, RecommendationFeedbackPolicy.AvoidStrength(rating), 6);
}
