using Maki.Core.Parsing;
using Maki.Core.Quality;

namespace Maki.Core.Tests;

public class ReleaseLanguageTests
{
    [Theory]
    [InlineData("ぼくの地球を守って 第01-21巻 [Boku no Chikyuu wo Mamotte vol 01-21]", "ja")]
    [InlineData("魔女と傭兵 第01巻", "ja")]
    [InlineData("Boku no Chikyuu wo Mamotte vol 01-21 [Raw]", "ja")]
    [InlineData("Boku no Chikyuu wo Mamotte v01 (JP)", "ja")]
    [InlineData("Boku no Chikyuu wo Mamotte v01 [Japanese]", "ja")]
    [InlineData("Boku no Chikyuu wo Mamotte v01 (Japanese Edition)", "ja")]
    [InlineData("Boku no Chikyuu wo Mamotte v01-21 [JPN-Raw]", "ja")]
    [InlineData("Boku no Chikyuu wo Mamotte v01 (Digital) [Raw-JP]", "ja")]
    [InlineData("Some Title v01 (English)", "en")]
    [InlineData("Some Title v01 [ENG]", "en")]
    [InlineData("나 혼자만 레벨업 v01", "ko")]
    public void Reads_the_language_a_release_says_it_is_in(string title, string expected) =>
        Assert.Equal(expected, ReleaseLanguage.Detect(title));

    [Theory]
    [InlineData("Paradise Kiss - 20th Anniversary Edition (2019) (Digital) (1r0n)")]
    [InlineData("The Witch and the Mercenary 001-015 (2024) (Digital) (danke-Empire)")]
    [InlineData("Boku no Chikyuu wo Mamotte vol 01-21")]
    [InlineData("Japanese Folktales v01 (Digital)")]
    [InlineData("Raw Hero v02 (Digital) (Group)")]
    [InlineData("魔女 傭兵 01巻")]
    [InlineData("Some Title v01 (Japanese-English)")]
    [InlineData("[Japanese] Some Title v01 [English]")]
    [InlineData("")]
    [InlineData(null)]
    public void An_untagged_latin_title_or_one_that_says_both_is_unknown(string? title) =>
        Assert.Null(ReleaseLanguage.Detect(title));

    private const string Untagged = "Paradise Kiss (2019) (Digital) (1r0n)";
    private const string English = "Paradise Kiss v01 (Digital) (English)";
    private const string Japanese = "Paradise Kiss v01 [Raw]";

    [Theory]
    [InlineData(new[] { "ja" }, English)]
    [InlineData(new[] { "ja-JP" }, English)]
    [InlineData(new[] { "ja" }, "나 혼자만 레벨업 v01")]
    [InlineData(new[] { "en" }, Japanese)]
    public void A_series_all_in_one_of_japanese_and_english_ignores_a_release_that_says_another_language(
        string[] files, string title) =>
        Assert.Equal((true, SpanVerdictReasons.LanguageMismatch), ReleaseLanguage.Judge(files, title));

    [Theory]
    [InlineData(Untagged)]
    [InlineData("魔女 傭兵 01巻")]
    [InlineData("Some Title v01 (Japanese-English)")]
    public void An_unknown_release_for_a_japanese_series_waits_for_a_click_and_is_not_rejected(string title) =>
        Assert.Equal((false, SpanVerdictReasons.LanguageUnknown), ReleaseLanguage.Judge(["ja"], title));

    [Theory]
    [InlineData(Untagged)]
    [InlineData(English)]
    [InlineData(Japanese)]
    public void A_series_with_japanese_and_other_files_never_takes_a_release_unattended(string title)
    {
        Assert.Equal((false, SpanVerdictReasons.LanguageMixed), ReleaseLanguage.Judge(["ja", "en"], title));
        Assert.Equal((false, SpanVerdictReasons.LanguageMixed), ReleaseLanguage.Judge(["ja", null], title));
    }

    [Theory]
    [InlineData(new[] { "ja" }, Japanese)]
    [InlineData(new[] { "en" }, Untagged)]
    [InlineData(new[] { "en" }, English)]
    [InlineData(new[] { "fr" }, Untagged)]
    [InlineData(new[] { "fr" }, Japanese)]
    [InlineData(new[] { "es", "fr" }, Untagged)]
    [InlineData(new string[0], Untagged)]
    public void Everything_else_is_left_as_it_was(string[] files, string title) =>
        Assert.Null(ReleaseLanguage.Judge(files, title));

    [Fact]
    public void Rows_with_no_language_are_left_as_they_were()
    {
        Assert.Null(ReleaseLanguage.Judge([null], Japanese));
        Assert.Null(ReleaseLanguage.Judge([null, "en"], Japanese));
    }
}
