using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class SourceLanguagesCanonicalTests
{
    [Theory]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("PT-BR", "pt-BR")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-hans", "zh-Hans")]
    [InlineData(" en ", "en")]
    [InlineData("JA", "ja")]
    [InlineData("es-LA", "es-la")]
    [InlineData("zh-hant", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData(null, "")]
    public void Canonical_picks_one_spelling(string? code, string expected)
    {
        Assert.Equal(expected, SourceLanguages.Canonical(code));
    }

    [Fact]
    public void Same_ignores_spelling_but_not_language()
    {
        Assert.True(SourceLanguages.Same("pt-BR", "pt-br"));
        Assert.True(SourceLanguages.Same("zh", "zh-Hans"));
        Assert.False(SourceLanguages.Same("pt", "pt-br"));
        Assert.False(SourceLanguages.Same("zh-Hant", "zh"));
    }
}
