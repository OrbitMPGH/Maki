using System.Text.Json;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Quality;

namespace Maki.Core.Tests;

public class PageConversionPlanTests
{
    /// <summary>A measured 20-page JPEG chapter at 2 bits per pixel: 20 pages of 1000 x 1500.</summary>
    private static PlanCandidate File(
        QualityTier? tier = QualityTier.Aggregator, string? source = "mangafire", string? format = "jpg",
        long size = 7_500_000, int? pages = 20, int? width = 1000, int? height = 1500, string? type = "manga",
        IReadOnlyList<string>? tags = null, PageConversionOverride mode = PageConversionOverride.Default) =>
        new(mode, type, tags ?? [], tier, source, format, size, pages, width, height);

    private static PageConversionConditions When(string json) => PageConversionConditions.Parse(json);

    [Fact]
    public void Bits_per_pixel_are_raw_and_unknown_without_a_measurement()
    {
        Assert.Equal(2.0, File().BitsPerPixel!.Value, 6);
        Assert.Equal(2.0, File(format: "png").BitsPerPixel!.Value, 6);
        Assert.Null(File(pages: null).BitsPerPixel);
        Assert.Null(File(pages: 0).BitsPerPixel);
        Assert.Null(File(width: null).BitsPerPixel);
        Assert.Null(File(height: 0).BitsPerPixel);
    }

    [Fact]
    public void Empty_conditions_match_every_file()
    {
        var empty = When("{}");

        Assert.True(empty.IsEmpty);
        Assert.True(empty.Matches(File()));
        Assert.True(empty.Matches(File(tier: null, source: null, format: null, pages: null, type: null)));
        Assert.True(When("""{"tiers":[],"sources":[" "]}""").IsEmpty);
    }

    [Theory]
    [InlineData("""{"tiers":["aggregator"]}""", true)]
    [InlineData("""{"tiers":["Volume","official"]}""", false)]
    [InlineData("""{"sources":["MangaFire"]}""", true)]
    [InlineData("""{"sources":["mangaf"]}""", false)]
    [InlineData("""{"sources":["manga*"]}""", true)]
    [InlineData("""{"sources":["torrent:*"]}""", false)]
    [InlineData("""{"formats":["jpg","png"]}""", true)]
    [InlineData("""{"formats":["mixed"]}""", false)]
    [InlineData("""{"seriesTypes":["Manga"]}""", true)]
    [InlineData("""{"seriesTypes":["manhwa"]}""", false)]
    [InlineData("""{"tags":["Keep-Original","other"]}""", true)]
    [InlineData("""{"tags":["colour"]}""", false)]
    [InlineData("""{"bppUnder":2.5}""", true)]
    [InlineData("""{"bppUnder":2.0}""", false)]
    [InlineData("""{"bppOver":1.5}""", true)]
    [InlineData("""{"bppOver":2.0}""", false)]
    [InlineData("""{"sizeOverBytes":7000000}""", true)]
    [InlineData("""{"sizeOverBytes":7500000}""", false)]
    [InlineData("""{"sizeUnderBytes":8000000}""", true)]
    [InlineData("""{"sizeUnderBytes":7500000}""", false)]
    [InlineData("""{"tiers":["aggregator"],"formats":["png"]}""", false)]
    [InlineData("""{"tiers":["aggregator"],"formats":["jpg"],"bppOver":1}""", true)]
    public void Every_field_that_is_set_has_to_match(string json, bool matches) =>
        Assert.Equal(matches, When(json).Matches(File(tags: ["keep-original"])));

    [Fact]
    public void A_torrent_glob_matches_every_torrent_source()
    {
        var torrents = When("""{"sources":["torrent:*"]}""");

        Assert.True(torrents.Matches(File(source: "torrent:nyaa")));
        Assert.True(torrents.Matches(File(source: "TORRENT:")));
        Assert.False(torrents.Matches(File(source: "torrentish")));
        Assert.False(torrents.Matches(File(source: null)));
    }

    [Fact]
    public void An_unmeasured_file_falls_through_format_and_bits_per_pixel_conditions()
    {
        var unmeasured = File(format: null, pages: null);

        Assert.False(When("""{"formats":["jpg"]}""").Matches(unmeasured));
        Assert.False(When("""{"bppUnder":100}""").Matches(unmeasured));
        Assert.False(When("""{"bppOver":0.001}""").Matches(unmeasured));
        Assert.True(When("""{"sizeOverBytes":1}""").Matches(unmeasured));
        Assert.False(When("""{"tiers":["unknown"]}""").Matches(File(tier: null)));
        Assert.False(When("""{"seriesTypes":["manga"]}""").Matches(File(type: null)));
    }

    [Fact]
    public void Conditions_round_trip_through_json_and_ignore_unknown_fields()
    {
        var conditions = When("""{"tiers":["Volume"],"sources":["import"],"bppUnder":1.5,"sizeOverBytes":100,"later":true}""");

        Assert.Equal(new[] { "volume" }, conditions.Tiers!);
        var json = conditions.ToJson();
        Assert.DoesNotContain("formats", json);
        Assert.DoesNotContain("isEmpty", json);
        var again = PageConversionConditions.Parse(json);
        Assert.Equal(json, again.ToJson());
        Assert.True(again.Matches(File(tier: QualityTier.Volume, source: "Import", pages: 20, size: 5_000_000)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"tiers":"volume"}""")]
    [InlineData("null")]
    [InlineData("")]
    public void Bad_json_does_not_parse(string json)
    {
        Assert.False(PageConversionConditions.TryParse(json, out _));
        if (json.Length > 0)
        {
            Assert.ThrowsAny<JsonException>(() => PageConversionConditions.Parse(json));
        }
    }

    private static readonly PageConversionPlan Plan = new(PageConversionFormat.Avif, PageConversionStop.High, true,
    [
        new PlanRule(1, "Volumes", When("""{"tiers":["volume"]}"""), false, PageConversionStop.Balanced),
        new PlanRule(0, "Scrapes", When("""{"tiers":["aggregator"]}"""), true, null),
        new PlanRule(2, "Big", When("""{"sizeOverBytes":1000}"""), false, PageConversionStop.Lossless)
    ]);

    [Fact]
    public void A_never_override_skips_whatever_the_rules_say()
    {
        var action = Plan.Resolve(File(tier: QualityTier.Volume, mode: PageConversionOverride.Never));

        Assert.Equal(new PlanAction.Skip("override"), action);
        Assert.Equal("skip", action.Name);
    }

    [Fact]
    public void A_stop_override_converts_at_that_stop_even_an_already_compact_file()
    {
        var action = Plan.Resolve(File(format: "webp", mode: PageConversionOverride.Compact));

        Assert.Equal(new PlanAction.Convert(new PageConversionSettings(PageConversionFormat.Avif, PageConversionStop.Compact, true),
            "override"), action);
        Assert.Equal("avif:compact+png", action.Name);
        Assert.Equal("lossless", Plan.Resolve(File(mode: PageConversionOverride.Lossless)).Name);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("AVIF")]
    public void A_file_already_stored_compact_is_skipped_before_the_rules(string format) =>
        Assert.Equal(new PlanAction.Skip("alreadycompact"), Plan.Resolve(File(tier: QualityTier.Volume, format: format)));

    [Fact]
    public void The_first_rule_in_order_decides()
    {
        Assert.Equal(new PlanAction.Skip("rule"), Plan.Resolve(File(tier: QualityTier.Aggregator)));

        var volume = Plan.Resolve(File(tier: QualityTier.Volume));
        Assert.Equal("avif:balanced+png", volume.Name);
        Assert.Equal("rule", Assert.IsType<PlanAction.Convert>(volume).Source);

        var big = Plan.Resolve(File(tier: QualityTier.Scanlator));
        Assert.Equal(new PlanAction.Convert(new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.Lossless, false),
            "rule"), big);
        Assert.Equal("lossless", big.Name);
    }

    [Fact]
    public void A_file_no_rule_matches_takes_the_default_stop()
    {
        var action = Plan.Resolve(File(tier: QualityTier.Official, size: 10));

        Assert.Equal(new PlanAction.Convert(new PageConversionSettings(PageConversionFormat.Avif, PageConversionStop.High, true),
            "default"), action);
        Assert.Equal("webp:high", PageConversionPlan.For(
            new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false)).Resolve(File()).Name);
    }
}
