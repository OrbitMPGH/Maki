using Maki.Core.Localization;

namespace Maki.Core.Tests;

public class SupportedLanguagesTests
{
    [Fact]
    public void DefaultIsOnTheList()
    {
        // Resolve() falls back to Default, so a Default that ships no catalogue would resolve every
        // unknown code to a language the app cannot actually draw itself in.
        Assert.Contains(SupportedLanguages.Default, SupportedLanguages.All);
    }

    [Fact]
    public void CodesAreUniqueAndTrimmed()
    {
        Assert.Equal(
            SupportedLanguages.All.Length,
            SupportedLanguages.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SupportedLanguages.All, c => Assert.Equal(c.Trim(), c));
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("zh-Hans", "zh-Hans")]
    public void MatchesAShippedCodeExactly(string input, string expected) =>
        Assert.Equal(expected, SupportedLanguages.Match(input));

    [Theory]
    [InlineData("DE", "de")]
    [InlineData("PT-br", "pt-BR")]
    public void MatchIsCaseInsensitiveButAnswersInCanonicalCasing(string input, string expected) =>
        Assert.Equal(expected, SupportedLanguages.Match(input));

    [Theory]
    [InlineData("de-AT", "de")]
    [InlineData("tr-CY", "tr")]
    [InlineData("fr-CA", "fr")]
    public void FallsBackToThePrimarySubtag(string input, string expected)
    {
        // A browser sending a regional tag should get the nearest catalogue rather than English.
        // SettingsController.SetUi refuses only codes that resolve to nothing, so this is also what
        // decides whether "de-AT" is a 400 or a stored "de".
        Assert.Equal(expected, SupportedLanguages.Match(input));
    }

    [Fact]
    public void PlainPortugueseResolvesToBrazilian()
    {
        // The only Portuguese shipped. A Brazilian interface reads far closer to a European
        // Portuguese speaker than an English one does, so the primary-subtag fallback is wanted here
        // rather than a miss.
        Assert.Equal("pt-BR", SupportedLanguages.Match("pt"));
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("xx-YY")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MatchAnswersNullForAnythingUnshipped(string? input) =>
        Assert.Null(SupportedLanguages.Match(input));

    [Fact]
    public void ResolveFloorsAtTheDefault() =>
        Assert.Equal(SupportedLanguages.Default, SupportedLanguages.Resolve("klingon"));
}
