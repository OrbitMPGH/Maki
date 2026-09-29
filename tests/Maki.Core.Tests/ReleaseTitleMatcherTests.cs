using Maki.Core.Entities;
using Maki.Core.Parsing;

namespace Maki.Core.Tests;

public class ReleaseTitleMatcherTests
{
    private static readonly ParsedReleaseTitle DualTitle = ReleaseTitleParser.Parse(
        "Aishiteru Game wo Owarasetai | I Want to End This Love Game v01-06 + 049.1-057 (2023-2025) (Digital) (1r0n)");

    [Fact]
    public void A_dual_title_matches_on_its_localized_half()
    {
        Assert.True(ReleaseTitleMatcher.Matches(DualTitle, "I Want to End This Love Game", null, []));
    }

    [Fact]
    public void A_romaji_series_title_matches_the_romaji_half()
    {
        Assert.True(ReleaseTitleMatcher.Matches(DualTitle, "Aishiteru Game wo Owarasetai", null, []));
    }

    [Fact]
    public void The_original_title_is_queried_alongside_the_display_title()
    {
        Assert.True(ReleaseTitleMatcher.Matches(DualTitle, "Completely Different Name", "Aishiteru Game wo Owarasetai", []));
    }

    [Fact]
    public void An_alt_title_matches_when_the_main_titles_do_not()
    {
        var release = ReleaseTitleParser.Parse("Sousou no Frieren v01 (Digital) (1r0n)");
        LocalizedTitle[] alts = [new("", "en"), new("Sousou no Frieren", "ja-ro")];

        Assert.True(ReleaseTitleMatcher.Matches(release, "Frieren: Beyond Journey's End", null, alts));
    }

    [Fact]
    public void An_appended_subtitle_still_matches()
    {
        var release = ReleaseTitleParser.Parse("Hajime no Ippo: Fighting Spirit! v01 (Digital)");

        Assert.True(ReleaseTitleMatcher.Matches(release, "Hajime no Ippo", null, []));
    }

    [Theory]
    [InlineData("She's Adopted a High School Boy! v01 (Digital) (1r0n)")]
    [InlineData("Magic, High School, and a Boy v01-03 (Digital)")]
    public void A_generic_short_title_does_not_match_a_longer_release(string title)
    {
        Assert.False(ReleaseTitleMatcher.Matches(ReleaseTitleParser.Parse(title), "High School Boy", null, []));
    }

    [Fact]
    public void An_unrelated_title_never_matches()
    {
        LocalizedTitle[] alts = [new("Chainsaw Man", "en")];

        Assert.False(ReleaseTitleMatcher.Matches(DualTitle, "Dandadan", "ダンダダン", alts));
    }

    [Fact]
    public void An_empty_release_never_matches()
    {
        Assert.False(ReleaseTitleMatcher.Matches(ReleaseTitleParser.Parse(""), "Title", null, []));
    }
}
