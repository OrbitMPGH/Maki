using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class ReaderPrefsTests
{
    [Fact]
    public void Sanitized_clamps_dim_and_falls_back_on_an_unknown_filter()
    {
        var spec = new ReaderPrefsSpec(Dim: 500, Filter: "blur").Sanitized();

        Assert.Equal(80, spec.Dim);
        Assert.Equal(ReaderPrefsSpec.FilterNone, spec.Filter);
        Assert.Equal(0, (new ReaderPrefsSpec(Dim: -5)).Sanitized().Dim);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("grayscale")]
    [InlineData("sepia")]
    [InlineData("invert")]
    public void Sanitized_keeps_a_known_filter(string filter)
    {
        Assert.Equal(filter, new ReaderPrefsSpec(Filter: filter).Sanitized().Filter);
    }

    [Fact]
    public void Parse_of_a_blob_from_before_the_tone_fields_uses_their_defaults()
    {
        var spec = ReaderPrefsSpec.Parse("""{"mode":"double","scale":150}""");

        Assert.Equal("double", spec.Mode);
        Assert.Equal(0, spec.Dim);
        Assert.Equal(ReaderPrefsSpec.FilterNone, spec.Filter);
        Assert.True(spec.KeepAwake);
    }

    [Fact]
    public void Serialize_round_trips_the_tone_fields()
    {
        var json = ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(Dim: 30, Filter: "sepia", KeepAwake: false));
        var spec = ReaderPrefsSpec.Parse(json);

        Assert.Equal(30, spec.Dim);
        Assert.Equal("sepia", spec.Filter);
        Assert.False(spec.KeepAwake);
    }

    [Fact]
    public void Sanitized_falls_back_on_an_unknown_split_mode()
    {
        Assert.Equal(ReaderPrefsSpec.SplitOff, new ReaderPrefsSpec(SplitWide: "quarter").Sanitized().SplitWide);
        Assert.Equal("rotate", new ReaderPrefsSpec(SplitWide: "rotate").Sanitized().SplitWide);
    }

    [Fact]
    public void Parse_of_an_older_blob_leaves_auto_webtoon_unset_for_the_layer_to_decide()
    {
        var spec = ReaderPrefsSpec.Parse("""{"mode":"paged"}""");

        Assert.Null(spec.AutoWebtoon);
        Assert.False(spec.CropBorders);
        Assert.Equal(ReaderPrefsSpec.SplitOff, spec.SplitWide);
        Assert.True(spec.WithAutoWebtoon(true).AutoWebtoon);
        Assert.False(spec.WithAutoWebtoon(false).AutoWebtoon);
        Assert.False((spec with { AutoWebtoon = false }).WithAutoWebtoon(true).AutoWebtoon);
    }

    [Fact]
    public void Serialize_round_trips_the_page_shape_fields()
    {
        var json = ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(SplitWide: "split", CropBorders: true, AutoWebtoon: false));
        var spec = ReaderPrefsSpec.Parse(json);

        Assert.Equal("split", spec.SplitWide);
        Assert.True(spec.CropBorders);
        Assert.False(spec.AutoWebtoon);
    }

    [Fact]
    public void Parse_of_a_blob_from_before_enhance_uses_off_and_the_balanced_preset()
    {
        var spec = ReaderPrefsSpec.Parse("""{"mode":"double","splitWide":"split"}""");

        Assert.Equal("off", spec.Enhance);
        Assert.Equal("a4k-m", spec.EnhanceMethod);
        Assert.Equal("a4k-m", spec.EnhanceFallback);
    }

    [Fact]
    public void Sanitized_maps_unknown_enhance_values_to_the_defaults()
    {
        var spec = new ReaderPrefsSpec(Enhance: "max", EnhanceMethod: "server:nope", EnhanceFallback: "server:span-s").Sanitized();

        Assert.Equal("off", spec.Enhance);
        Assert.Equal("a4k-m", spec.EnhanceMethod);
        Assert.Equal("a4k-m", spec.EnhanceFallback);
    }

    [Theory]
    [InlineData("a4k-s")]
    [InlineData("a4k-m")]
    [InlineData("a4k-a")]
    [InlineData("off")]
    public void Sanitized_keeps_every_known_enhance_fallback(string fallback)
    {
        Assert.Equal(fallback, new ReaderPrefsSpec(EnhanceFallback: fallback).Sanitized().EnhanceFallback);
    }

    [Theory]
    [InlineData("a4k-s")]
    [InlineData("a4k-m")]
    [InlineData("a4k-a")]
    [InlineData("server:span-s")]
    [InlineData("server:realesrgan-av3")]
    public void Sanitized_keeps_every_known_enhance_method(string method)
    {
        Assert.Equal(method, new ReaderPrefsSpec(EnhanceMethod: method).Sanitized().EnhanceMethod);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("auto")]
    [InlineData("always")]
    public void Sanitized_keeps_every_known_enhance_mode(string mode)
    {
        Assert.Equal(mode, new ReaderPrefsSpec(Enhance: mode).Sanitized().Enhance);
    }

    [Fact]
    public void Serialize_round_trips_the_enhance_fields()
    {
        var json = ReaderPrefsSpec.Serialize(
            new ReaderPrefsSpec(Enhance: "auto", EnhanceMethod: "server:span-s", EnhanceFallback: "a4k-a"));
        var spec = ReaderPrefsSpec.Parse(json);

        Assert.Equal("auto", spec.Enhance);
        Assert.Equal("server:span-s", spec.EnhanceMethod);
        Assert.Equal("a4k-a", spec.EnhanceFallback);
        Assert.Contains("\"enhanceMethod\":\"server:span-s\"", json);
    }

    [Fact]
    public void Parse_of_a_blob_without_skip_tiers_skips_volumes()
    {
        Assert.Equal(["volume"], ReaderPrefsSpec.Parse("""{"enhance":"always"}""").EnhanceSkipTiers);
        Assert.Equal(["volume"], ReaderPrefsSpec.Parse(null).EnhanceSkipTiers);
    }

    [Fact]
    public void Sanitized_keeps_an_empty_skip_list_and_drops_unknown_tiers()
    {
        Assert.Empty(new ReaderPrefsSpec(EnhanceSkipTiers: []).Sanitized().EnhanceSkipTiers!);
        Assert.Equal(
            ["official", "volume"],
            new ReaderPrefsSpec(EnhanceSkipTiers: ["volume", "bogus", "official", "volume"]).Sanitized().EnhanceSkipTiers);
    }

    [Fact]
    public void Serialize_round_trips_the_skip_tiers()
    {
        var spec = ReaderPrefsSpec.Parse(ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(EnhanceSkipTiers: ["official"])));

        Assert.Equal(["official"], spec.EnhanceSkipTiers);
    }
}
