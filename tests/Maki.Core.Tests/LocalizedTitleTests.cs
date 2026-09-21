using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Core.Tests;

/// <summary>
/// Picking a title by language, which is what a display-language preference and ComicInfo's
/// LocalizedSeries both do.
/// </summary>
public class LocalizedTitleTests
{
    private static readonly LocalizedTitle[] Berserk =
    [
        new("Berserk", "en"),
        new("ベルセルク", "ja"),
        new("Berserk (Edición Deluxe)", "es"),
        new("Berserk Deluxe", null),
    ];

    [Fact]
    public void Pick_follows_the_preference_order_not_the_list_order()
    {
        Assert.Equal("ベルセルク", LocalizedTitle.Pick(Berserk, ["ja", "en"]));
        Assert.Equal("Berserk", LocalizedTitle.Pick(Berserk, ["en", "ja"]));
    }

    [Fact]
    public void Pick_falls_through_to_the_next_preference()
    {
        Assert.Equal("Berserk (Edición Deluxe)", LocalizedTitle.Pick(Berserk, ["fr", "es"]));
    }

    [Fact]
    public void Pick_is_null_when_nothing_matches()
    {
        Assert.Null(LocalizedTitle.Pick(Berserk, ["ko"]));
        Assert.Null(LocalizedTitle.Pick([], ["en"]));
    }

    [Fact]
    public void An_untagged_title_answers_no_preference_at_all()
    {
        // "Berserk Deluxe" carries no code, so it can never be selected — including by a caller
        // that passes an empty string, which would otherwise match it and every other untagged one.
        Assert.Null(LocalizedTitle.Pick(Berserk, [""]));
    }

    [Theory]
    [InlineData("en", "EN", true)]
    [InlineData("pt-br", "pt", true)]
    [InlineData("es-la", "es", true)]
    // The other direction is not a match: a reader who asked for Brazilian Portuguese and got the
    // European edition is reading a different translation than the one they chose.
    [InlineData("pt", "pt-br", false)]
    [InlineData("en", "es", false)]
    // A prefix that isn't a whole subtag: "e" must not reach "en".
    [InlineData("en", "e", false)]
    // A *script* variant is a different script, not a regional flavour: MangaBaka tags
    // romanizations "ja-Latn", and someone who asked for Japanese did not ask for those.
    [InlineData("ja-latn", "ja", false)]
    [InlineData("ja-latn", "ja-latn", true)]
    public void Matches_is_case_insensitive_and_accepts_a_region_for_a_bare_code(
        string language, string wanted, bool expected) =>
        Assert.Equal(expected, LocalizedTitle.Matches(language, wanted));

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("ja", new[] { "ja" })]
    [InlineData(" ja , en ", new[] { "ja", "en" })]
    [InlineData("ja,,en", new[] { "ja", "en" })]
    public void ParsePreference_splits_and_trims(string? setting, string[] expected) =>
        Assert.Equal(expected, LocalizedTitle.ParsePreference(setting));
}

/// <summary>The <c>languageFilter</c> every source takes and every source mapping stores.</summary>
public class SourceLanguagesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_filter_means_english_not_every_language(string? filter) =>
        // The load-bearing default: a mapping nobody has touched has to keep listing exactly what it
        // listed before, or every existing series grows a chapter row per translation on next sync.
        Assert.Equal(["en"], SourceLanguages.Parse(filter));

    [Fact]
    public void Parse_lowercases_and_keeps_the_given_order()
    {
        Assert.Equal(["es", "en"], SourceLanguages.Parse("ES, en"));
        Assert.Equal(["en", "es"], SourceLanguages.Parse("en,es"));
    }

    [Fact]
    public void Parse_drops_repeats()
    {
        Assert.Equal(["en", "es"], SourceLanguages.Parse("en,ES,es,en"));
    }

    [Fact]
    public void Serialize_stores_null_for_the_plain_default()
    {
        // So an untouched mapping round-trips to the null it already holds rather than to "en",
        // which SourceMappingController compares against to decide whether the filter changed.
        Assert.Null(SourceLanguages.Serialize(["en"]));
        Assert.Equal("en,es", SourceLanguages.Serialize(["en", "es"]));
        Assert.Equal("es", SourceLanguages.Serialize(["es"]));
    }

    [Fact]
    public void Includes_accepts_a_regional_code_for_a_bare_one()
    {
        Assert.True(SourceLanguages.Includes(["en", "pt"], "pt-br"));
        Assert.True(SourceLanguages.Includes(["en"], "EN"));
        Assert.False(SourceLanguages.Includes(["en"], "es"));
        Assert.False(SourceLanguages.Includes(["en"], null));
    }
}
