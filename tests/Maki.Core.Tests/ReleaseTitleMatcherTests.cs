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

    private static readonly string[] DressUpDarling = ["My Dress-Up Darling", "Sono Bisque Doll wa Koi o Suru"];

    private static readonly string[] Xoxo =
        ["My Dress-Up Darling XOXO!", "Bisque Doll de Chu", "Smooch with a Bisque Doll"];

    [Fact]
    public void A_release_naming_a_spin_off_belongs_to_the_spin_off()
    {
        var release = ReleaseTitleParser.Parse(
            "My Dress-Up Darling XOXO! 001-013 as v01 (Digital-Compilation) (Oak) [Completed]");

        var match = ReleaseTitleMatcher.Match(release, DressUpDarling, Xoxo);

        Assert.False(match.Matched);
        Assert.Equal("My Dress-Up Darling XOXO!", match.Rival);
        Assert.True(ReleaseTitleMatcher.Match(release, DressUpDarling, []).Matched);
    }

    [Fact]
    public void A_release_of_ours_still_matches_with_the_spin_off_as_a_rival()
    {
        var match = ReleaseTitleMatcher.Match(
            ReleaseTitleParser.Parse("My Dress-Up Darling v01 (Digital) (1r0n)"), DressUpDarling, Xoxo);

        Assert.True(match.Matched);
        Assert.Null(match.Rival);
    }

    [Fact]
    public void A_prequel_with_the_shorter_title_takes_its_own_release()
    {
        string[] ours = ["Tokyo Ghoul:re"];
        string[] rivals = ["Tokyo Ghoul"];

        Assert.False(ReleaseTitleMatcher.Match(ReleaseTitleParser.Parse("Tokyo Ghoul v01 (Digital)"), ours, rivals).Matched);
        Assert.True(ReleaseTitleMatcher.Match(ReleaseTitleParser.Parse("Tokyo Ghoul:re v01 (Digital)"), ours, rivals).Matched);
    }

    [Fact]
    public void A_rival_scoring_the_same_as_our_title_keeps_the_release_ours()
    {
        var match = ReleaseTitleMatcher.Match(
            ReleaseTitleParser.Parse("Kaguya-sama v01 (Digital)"), ["Kaguya-sama"], ["Kaguya sama"]);

        Assert.True(match.Matched);
    }

    [Fact]
    public void A_rival_with_a_different_word_set_does_not_compete()
    {
        var match = ReleaseTitleMatcher.Match(
            ReleaseTitleParser.Parse("Hajime no Ippo: Fighting Spirit! v01 (Digital)"),
            ["Hajime no Ippo"], ["Hajime no Ippo Fighting Spirits Gaiden"]);

        Assert.True(match.Matched);
    }

    [Fact]
    public void A_dual_title_release_is_judged_by_the_half_that_names_us()
    {
        var ours = ReleaseTitleParser.Parse("Sono Bisque Doll wa Koi o Suru | My Dress-Up Darling v01 (Digital)");
        var spinOff = ReleaseTitleParser.Parse("Bisque Doll de Chu | My Dress-Up Darling XOXO! v01 (Digital)");

        Assert.True(ReleaseTitleMatcher.Match(ours, DressUpDarling, Xoxo).Matched);
        Assert.False(ReleaseTitleMatcher.Match(spinOff, DressUpDarling, Xoxo).Matched);
    }

    [Fact]
    public void Rivals_keeps_only_whole_word_extensions_that_are_not_our_own_spellings()
    {
        var rivals = ReleaseTitleMatcher.Rivals(["Blue Lock"],
            ["Blue Lock: Episode Nagi", "Blue Locker", "Lock Blue", "BLUE LOCK", "Blue", ""]);

        Assert.Equal(["Blue Lock: Episode Nagi", "Blue"], rivals);
    }
}
