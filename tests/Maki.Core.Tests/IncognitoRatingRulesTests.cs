using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Core.Tests;

/// <summary>
/// The per-content-rating incognito defaults a newly added series starts at.
/// </summary>
public class IncognitoRatingRulesTests
{
    [Fact]
    public void Unset_falls_back_to_the_shipped_default()
    {
        var rules = IncognitoRatingRules.Parse(null);

        Assert.Equal(IncognitoMode.Full, IncognitoRatingRules.Resolve(rules, "pornographic"));
        Assert.Equal(IncognitoMode.Off, IncognitoRatingRules.Resolve(rules, "erotica"));
        Assert.Equal(IncognitoMode.Off, IncognitoRatingRules.Resolve(rules, "safe"));
    }

    [Fact]
    public void Empty_object_means_every_rating_off_and_is_not_the_default()
    {
        var rules = IncognitoRatingRules.Parse("{}");

        Assert.Equal(IncognitoMode.Off, IncognitoRatingRules.Resolve(rules, "pornographic"));
    }

    [Fact]
    public void Round_trips_through_serialization()
    {
        var stored = IncognitoRatingRules.Serialize(new Dictionary<string, IncognitoMode>
        {
            ["erotica"] = IncognitoMode.ScrobbleOnly,
            ["pornographic"] = IncognitoMode.Full,
        });

        var rules = IncognitoRatingRules.Parse(stored);

        Assert.Equal(IncognitoMode.ScrobbleOnly, IncognitoRatingRules.Resolve(rules, "erotica"));
        Assert.Equal(IncognitoMode.Full, IncognitoRatingRules.Resolve(rules, "pornographic"));
        Assert.Equal(IncognitoMode.Off, IncognitoRatingRules.Resolve(rules, "suggestive"));
    }

    [Fact]
    public void Unreadable_blob_falls_back_rather_than_throwing_the_add_away()
    {
        Assert.Equal(IncognitoMode.Full, IncognitoRatingRules.Resolve(
            IncognitoRatingRules.Parse("not json"), "pornographic"));
    }

    [Fact]
    public void Unknown_mode_names_are_dropped_leaving_that_rating_off()
    {
        var rules = IncognitoRatingRules.Parse("""{"pornographic":"Sometimes"}""");

        Assert.Equal(IncognitoMode.Off, IncognitoRatingRules.Resolve(rules, "pornographic"));
    }

    [Fact]
    public void A_series_with_no_content_rating_yet_matches_nothing()
    {
        Assert.Equal(IncognitoMode.Off, IncognitoRatingRules.Resolve(
            IncognitoRatingRules.Parse(null), null));
    }
}
