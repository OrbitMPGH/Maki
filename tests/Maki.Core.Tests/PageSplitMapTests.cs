using Maki.Core.Entities;
using Maki.Core.Images;

namespace Maki.Core.Tests;

public class PageSplitMapTests
{
    // Old pages 0..5; page 2 became three pages and page 4 two: new pages 0..8.
    private static readonly PageSplitMap Map = PageSplitMap.Parse("2:3,4:2")!;

    [Fact]
    public void Old_pages_map_to_where_their_pieces_start_and_back()
    {
        Assert.Equal([0, 1, 2, 5, 6, 8], Enumerable.Range(0, 6).Select(Map.NewStart));
        Assert.Equal(9, Map.NewStart(6));
        Assert.Equal([0, 1, 2, 2, 2, 3, 4, 4, 5], Enumerable.Range(0, 9).Select(Map.OldIndex));
    }

    [Fact]
    public void A_position_in_a_tall_page_lands_in_the_piece_it_falls_in_and_comes_back()
    {
        Assert.Equal((2, 0.0), Map.Forward(2, 0));
        var (page, offset) = Map.Forward(2, 0.5);
        Assert.Equal(3, page);
        Assert.Equal(0.5, offset, 6);
        Assert.Equal(4, Map.Forward(2, 1).Page);
        Assert.Equal((5, 0.25), Map.Forward(3, 0.25));

        var (old, back) = Map.Backward(3, 0.5);
        Assert.Equal(2, old);
        Assert.Equal(0.5, back, 6);
        Assert.Equal((5, 0.3), Map.Backward(8, 0.3));
    }

    [Fact]
    public void A_later_split_of_the_pieces_composes_with_the_first()
    {
        // The second split cuts new page 4 (the last piece of old page 2) and new page 8 (old page 5) in two.
        var composed = Map.Then(PageSplitMap.Parse("4:2,8:2")!);

        Assert.Equal("2:4,4:2,5:2", composed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("3:1")]
    public void An_empty_map_parses(string text) => Assert.True(PageSplitMap.Parse(text)!.IsEmpty);

    [Theory]
    [InlineData("1")]
    [InlineData("a:2")]
    [InlineData("1:0")]
    [InlineData("1:2,1:3")]
    public void Anything_else_does_not(string text) => Assert.Null(PageSplitMap.Parse(text));

    [Theory]
    [InlineData(PageConversionFormat.Webp, PageConversionStop.High, false, 5000, false, "webp:high+split5000")]
    [InlineData(PageConversionFormat.Avif, PageConversionStop.Compact, true, 4000, false, "avif:compact+png+split4000")]
    [InlineData(PageConversionFormat.Webp, PageConversionStop.Lossless, false, 6000, false, "lossless+split6000")]
    public void A_split_name_parses_back(PageConversionFormat format, PageConversionStop stop, bool png, int height,
        bool only, string name)
    {
        var settings = new PageConversionSettings(format, stop, png, height, only);

        Assert.Equal(name, settings.Name());
        Assert.Equal(settings, PageConversionSettings.Parse(name));
    }

    [Fact]
    public void A_split_only_name_carries_only_the_height()
    {
        Assert.Equal("split5000",
            new PageConversionSettings(PageConversionFormat.Avif, PageConversionStop.Compact, false, 5000, true).Name());
        var parsed = PageConversionSettings.Parse("split5000")!;
        Assert.Equal((5000, true), (parsed.SplitHeight, parsed.SplitOnly));
        Assert.Null(PageConversionSettings.Parse("webp:high+splitx"));
        Assert.Null(PageConversionSettings.Parse("split0"));
    }

    private static PlanCandidate File(string format = "jpg", PageConversionOverride mode = PageConversionOverride.Default) =>
        new(mode, "manhwa", [], null, "src", format, 1_000_000, 10, 800, 1200);

    [Fact]
    public void A_plan_that_splits_cuts_webp_files_and_converted_ones_without_converting_them()
    {
        var plan = new PageConversionPlan(PageConversionFormat.Webp, PageConversionStop.Balanced, false, [], 5000);

        Assert.Equal("webp:balanced+split5000", plan.Resolve(File()).Name);
        var compact = Assert.IsType<PlanAction.Convert>(plan.Resolve(File("webp")));
        Assert.Equal(("split", true, 5000), (compact.Source, compact.Settings.SplitOnly, compact.Settings.SplitHeight));
        Assert.Equal("split5000", plan.ResolveConverted(File("webp")).Name);
        Assert.Equal("skip", plan.ResolveConverted(File(mode: PageConversionOverride.Never)).Name);

        var whole = plan with { SplitHeight = null };
        Assert.Equal("skip", whole.Resolve(File("webp")).Name);
        Assert.Equal(new PlanAction.Skip("converted"), whole.ResolveConverted(File("webp")));
    }

    [Fact]
    public void A_skip_rule_keeps_a_split_off_its_files()
    {
        var rule = new PlanRule(0, "Keep", PageConversionConditions.Parse("""{"seriesTypes":["manhwa"]}"""), true, null);
        var plan = new PageConversionPlan(PageConversionFormat.Webp, PageConversionStop.High, false, [rule], 5000);

        Assert.Equal("skip", plan.Resolve(File("webp")).Name);
        Assert.Equal("skip", plan.ResolveConverted(File("webp")).Name);
    }
}
