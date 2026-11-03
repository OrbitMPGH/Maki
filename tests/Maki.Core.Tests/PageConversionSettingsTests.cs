using Maki.Core.Images;

namespace Maki.Core.Tests;

public class PageConversionSettingsTests
{
    [Theory]
    [InlineData(PageConversionFormat.Webp, PageConversionStop.High, false, "webp:high")]
    [InlineData(PageConversionFormat.Avif, PageConversionStop.Balanced, false, "avif:balanced")]
    [InlineData(PageConversionFormat.Webp, PageConversionStop.Smallest, true, "webp:smallest+png")]
    [InlineData(PageConversionFormat.Avif, PageConversionStop.Compact, true, "avif:compact+png")]
    public void A_name_parses_back_to_the_same_settings(PageConversionFormat format, PageConversionStop stop, bool png, string name)
    {
        var settings = new PageConversionSettings(format, stop, png);

        Assert.Equal(name, settings.Name());
        Assert.Equal(settings, PageConversionSettings.Parse(name));
    }

    [Fact]
    public void Lossless_has_one_name_whatever_the_format()
    {
        Assert.Equal("lossless", new PageConversionSettings(PageConversionFormat.Avif, PageConversionStop.Lossless, true).Name());
        Assert.Equal(PageConversionStop.Lossless, PageConversionSettings.Parse("lossless")!.Stop);
    }

    [Theory]
    [InlineData("")]
    [InlineData("webp")]
    [InlineData("jxl:high")]
    [InlineData("webp:best")]
    [InlineData("webp:lossless")]
    [InlineData(null)]
    public void Anything_else_does_not_parse(string? name) => Assert.Null(PageConversionSettings.Parse(name));

    [Theory]
    [InlineData(PageConversionFormat.Webp, 80, PageConversionStop.Balanced)]
    [InlineData(PageConversionFormat.Webp, 60, PageConversionStop.Compact)]
    [InlineData(PageConversionFormat.Webp, 85, PageConversionStop.High)]
    [InlineData(PageConversionFormat.Webp, 100, PageConversionStop.High)]
    [InlineData(PageConversionFormat.Webp, 1, PageConversionStop.Smallest)]
    [InlineData(PageConversionFormat.Avif, 55, PageConversionStop.Compact)]
    [InlineData(PageConversionFormat.Avif, 72, PageConversionStop.Balanced)]
    [InlineData(PageConversionFormat.Avif, 73, PageConversionStop.High)]
    public void A_quality_lands_on_its_nearest_stop_and_a_tie_goes_up(PageConversionFormat format, int quality,
        PageConversionStop stop) =>
        Assert.Equal(stop, PageConversionSettings.NearestStop(format, quality));

    [Fact]
    public void Lossless_has_no_quality() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PageConversionSettings.Quality(PageConversionFormat.Webp, PageConversionStop.Lossless));
}
