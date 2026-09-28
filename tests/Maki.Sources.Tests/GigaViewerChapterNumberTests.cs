using System.Globalization;
using Maki.Sources.GigaViewer;

namespace Maki.Sources.Tests;

public class GigaViewerChapterNumberTests
{
    // Expected values are strings, decimal.Parse'd below: decimal isn't a legal attribute
    // argument type, and a double literal here would round-trip through binary floating point
    // and stop comparing equal to the pure-decimal arithmetic GigaViewerChapterNumber does.
    [Theory]
    [InlineData("140話", "140")]
    [InlineData("139話 後編", "139.3")]
    [InlineData("139話 前編", "139.1")]
    [InlineData("第７３話　思い出スフレ", "73")]
    [InlineData("第154回「パパの存在」", "154")]
    [InlineData("裏エステガール編 第8話(前編)", "8.1")]
    [InlineData("番外編㉚　空子さん", null)]
    [InlineData("Vol.25", "25")]
    [InlineData("[特別読切] 昔フッた", null)]
    [InlineData("第60話 後編②", "60.32")]
    [InlineData("第73話②", "73.2")]
    public void Parse_MatchesTheDocumentedSamples(string title, string? expected)
    {
        var expectedNumber = expected is null ? (decimal?)null : decimal.Parse(expected, CultureInfo.InvariantCulture);
        Assert.Equal(expectedNumber, GigaViewerChapterNumber.Parse(title));
    }

    [Fact]
    public void Parse_ReturnsNullForBlank()
    {
        Assert.Null(GigaViewerChapterNumber.Parse(null));
        Assert.Null(GigaViewerChapterNumber.Parse(""));
        Assert.Null(GigaViewerChapterNumber.Parse("   "));
    }
}
