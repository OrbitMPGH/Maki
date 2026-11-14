using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Quality;

namespace Maki.Core.Tests;

public class PageConversionColourTargetTests
{
    private static readonly PageTarget AvifBalanced = new(PageConversionFormat.Avif, PageConversionStop.Balanced);

    /// <summary>Webp High for black-and-white pages, AVIF Balanced for colour ones.</summary>
    private static readonly PageConversionSettings Split =
        new(PageConversionFormat.Webp, PageConversionStop.High, false, Colour: AvifBalanced);

    /// <summary>A colour page: a hue sweep with grain, well past the colour check's chroma floor.</summary>
    private static Raster Painted(int width, int height, int seed)
    {
        var image = new Raster(width, height, bands: 3);
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var grain = random.Next(24);
            image[x, y] = new Rgba((byte)Math.Clamp(x * 220 / width + grain, 0, 255), (byte)(40 + grain),
                (byte)Math.Clamp(y * 220 / height + grain, 0, 255));
        }

        return image;
    }

    private static PlanCandidate File() =>
        new(PageConversionOverride.Default, "manga", [], QualityTier.Aggregator, "mangafire", "jpg", 7_500_000, 20, 1000, 1500);

    [Theory]
    [InlineData("webp:high/avif:balanced")]
    [InlineData("webp:high/avif:balanced+png")]
    [InlineData("lossless/avif:high+png")]
    [InlineData("avif:compact/lossless+png+split5000")]
    public void A_colour_target_rides_in_the_name_and_parses_back(string name)
    {
        var settings = PageConversionSettings.Parse(name)!;

        Assert.True(settings.ByColour);
        Assert.Equal(name, settings.Name());
    }

    [Theory]
    [InlineData("webp:high/webp:high")]
    [InlineData("webp:high/")]
    [InlineData("webp:high/jxl:high")]
    [InlineData("lossless/lossless")]
    [InlineData("lossless+png")]
    [InlineData("webp:high/avif:balanced/avif:high")]
    public void A_broken_or_redundant_colour_target_does_not_parse(string name) =>
        Assert.Null(PageConversionSettings.Parse(name));

    [Fact]
    public void A_colour_target_equal_to_the_other_changes_nothing()
    {
        var same = new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, true,
            Colour: new PageTarget(PageConversionFormat.Webp, PageConversionStop.High));

        Assert.False(same.ByColour);
        Assert.Equal("webp:high+png", same.Name());
    }

    [Fact]
    public void Colour_and_grey_pages_take_their_own_target()
    {
        var colour = PageConversion.Convert(Painted(600, 900, 1).EncodeJpeg(95), ".jpg", Split);
        var grey = PageConversion.Convert(ArchivePageConverterTests.Scan(600, 900, 2).EncodeJpeg(95), ".jpg", Split);

        Assert.True(colour.Converted, colour.Skipped?.ToString());
        Assert.True(colour.Colour);
        Assert.Equal(".avif", colour.Extension);
        Assert.True(grey.Converted, grey.Skipped?.ToString());
        Assert.False(grey.Colour);
        Assert.Equal(".webp", grey.Extension);
    }

    [Fact]
    public void A_lossless_colour_target_keeps_colour_jpegs_and_converts_grey_ones()
    {
        var settings = new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.Balanced, false,
            Colour: new PageTarget(PageConversionFormat.Webp, PageConversionStop.Lossless));
        var jpeg = Painted(600, 900, 3).EncodeJpeg(95);

        var colour = PageConversion.Convert(jpeg, ".jpg", settings);
        var grey = PageConversion.Convert(ArchivePageConverterTests.Scan(600, 900, 4).EncodeJpeg(95), ".jpg", settings);

        Assert.Equal(PageSkip.Lossless, colour.Skipped);
        Assert.Same(jpeg, colour.Bytes);
        Assert.True(grey.Converted, grey.Skipped?.ToString());
    }

    [Fact]
    public void Without_a_colour_target_no_page_is_checked_for_colour()
    {
        var page = PageConversion.Convert(Painted(600, 900, 5).EncodeJpeg(95), ".jpg",
            new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false));

        Assert.True(page.Converted);
        Assert.False(page.Colour);
        Assert.Equal(".webp", page.Extension);
    }

    [Fact]
    public void The_gate_weighs_the_heavier_target_until_the_page_is_decoded()
    {
        var colourPage = Painted(600, 900, 6).EncodeJpeg(95);
        var onlyWebp = new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false);

        var split = PageConversion.WeightOf(colourPage, ".jpg", Split);
        var webp = PageConversion.WeightOf(colourPage, ".jpg", onlyWebp);

        Assert.True(split.Avif);
        Assert.False(webp.Avif);
        Assert.True(split.Megabytes > webp.Megabytes);
    }

    [Fact]
    public void A_one_band_page_can_only_take_the_black_and_white_target()
    {
        using var grey = NetVips.Image.NewFromMemory(new byte[600 * 900], 600, 900, 1, NetVips.Enums.BandFormat.Uchar);
        var bytes = (grey + 128).Cast(NetVips.Enums.BandFormat.Uchar).JpegsaveBuffer(q: 95);

        Assert.False(PageConversion.WeightOf(bytes, ".jpg", Split).Avif);
    }

    [Fact]
    public void A_tall_colour_strip_is_split_into_colour_target_pieces()
    {
        var strip = TallStrips.Jpeg(800, 12000, [(5900, 5960)]);
        var settings = Split with { SplitHeight = 6000 };

        var pieces = PageConversion.ConvertPage(strip, ".jpg", settings);

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p =>
        {
            Assert.True(p.Colour);
            Assert.Equal(".avif", p.Extension);
        });
    }

    [Fact]
    public void A_rule_converts_with_its_own_targets_and_the_default_with_the_plans()
    {
        var rule = new PlanRule(0, "High tier", PageConversionConditions.Parse("""{"tiers":["aggregator"]}"""), false,
            PageConversionStop.Compact, PageConversionFormat.Avif, new PageTarget(PageConversionFormat.Webp, PageConversionStop.High));
        var plan = new PageConversionPlan(PageConversionFormat.Webp, PageConversionStop.Balanced, true, [rule],
            DefaultColour: AvifBalanced);

        var byRule = Assert.IsType<PlanAction.Convert>(plan.Resolve(File()));
        var byDefault = Assert.IsType<PlanAction.Convert>((plan with { Rules = [] }).Resolve(File()));

        Assert.Equal("avif:compact/webp:high+png", byRule.Settings.Name());
        Assert.Equal("webp:balanced/avif:balanced+png", byDefault.Settings.Name());
    }

    [Fact]
    public void A_rule_without_a_format_or_colour_target_keeps_converting_as_before()
    {
        var rule = new PlanRule(0, "Old rule", PageConversionConditions.Parse("{}"), false, PageConversionStop.High);
        var plan = new PageConversionPlan(PageConversionFormat.Avif, PageConversionStop.Balanced, false, [rule]);

        var action = Assert.IsType<PlanAction.Convert>(plan.Resolve(File()));

        Assert.Equal("avif:high", action.Settings.Name());
    }

    [Fact]
    public void A_series_override_moves_both_targets_to_its_stop_in_the_default_formats()
    {
        var plan = new PageConversionPlan(PageConversionFormat.Webp, PageConversionStop.Balanced, false, [],
            DefaultColour: AvifBalanced);
        var file = File() with { SeriesOverride = PageConversionOverride.Smallest };

        var action = Assert.IsType<PlanAction.Convert>(plan.Resolve(file));

        Assert.Equal("webp:smallest/avif:smallest", action.Settings.Name());
    }

    [Fact]
    public void Split_only_settings_keep_the_colour_targets_quality_and_never_lossless()
    {
        var plan = new PageConversionPlan(PageConversionFormat.Webp, PageConversionStop.Lossless, false, [], 5000,
            AvifBalanced with { Stop = PageConversionStop.Lossless });

        var settings = plan.SplitOnlySettings;

        Assert.Equal(PageConversionStop.High, settings.Grey.Stop);
        Assert.Equal(PageConversionStop.High, settings.ColourTarget.Stop);
        Assert.Equal("split5000", settings.Name());
    }
}
