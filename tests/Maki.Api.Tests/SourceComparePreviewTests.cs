using Maki.Api.Services;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>
/// The two decisions the source comparison makes before it touches the network: how many pages are
/// worth sampling, and which chapter every source gets asked for.
/// </summary>
public class SourceComparePreviewTests
{
    private static SourceChapter Chapter(decimal number) =>
        new("fake", "s", number.ToString(), number.ToString(), number, null, null, "en", null);

    private static IReadOnlyList<SourceChapter> Listing(params decimal[] numbers) =>
        [.. numbers.Select(Chapter)];

    [Theory]
    // One full-width panel is a screenful of art, which is all there is to judge on a long strip.
    [InlineData("manhwa", 1)]
    [InlineData("manhua", 1)]
    [InlineData("Webtoon", 1)]
    [InlineData("Long Strip", 1)]
    // Page-based work differs in lettering and typesetting, which one page doesn't show.
    [InlineData("manga", 3)]
    [InlineData("one shot", 3)]
    [InlineData("", 3)]
    [InlineData(null, 3)]
    public void Sample_count_follows_the_series_type(string? type, int expected) =>
        Assert.Equal(expected, SourceComparePreviewService.SampleCountFor(type));

    [Fact]
    public void Picks_chapter_one_when_every_source_has_it()
    {
        // The ordinary case, and the reason the rule exists: chapter 1 is the one everybody
        // carries and the only one that spoils nothing.
        var (_, target) = SourceComparePreviewService.PlanChapter(
            [Listing(1, 2, 3), Listing(1, 2, 3, 4)], null);

        Assert.Equal(1m, target);
    }

    [Fact]
    public void A_source_with_a_short_catalogue_does_not_drag_everyone_to_chapter_one()
    {
        // MangaDex drops old fan scans, so it can carry only the newest handful of a 1400-chapter
        // series. Plain "lowest number anyone lists" would put every comparison on a chapter that
        // exactly one source can show.
        var (_, target) = SourceComparePreviewService.PlanChapter(
            [Listing(1, 2, 3, 1488, 1489), Listing(1488, 1489)], null);

        Assert.Equal(1488m, target);
    }

    [Fact]
    public void Ties_on_agreement_are_broken_by_the_lower_chapter()
    {
        var (_, target) = SourceComparePreviewService.PlanChapter(
            [Listing(5, 6, 7), Listing(5, 6, 7)], null);

        Assert.Equal(5m, target);
    }

    [Fact]
    public void A_requested_chapter_wins_over_the_automatic_pick()
    {
        var (_, target) = SourceComparePreviewService.PlanChapter(
            [Listing(1, 2, 3), Listing(1, 2, 3)], 3m);

        Assert.Equal(3m, target);
    }

    [Fact]
    public void Picker_offers_the_union_so_chapter_one_stays_selectable()
    {
        // The automatic pick lands on 1488 here, but the user must still be able to ask for
        // chapter 1 and see the one source that has it.
        var (picker, target) = SourceComparePreviewService.PlanChapter(
            [Listing(1, 2, 1488), Listing(1488)], null);

        Assert.Equal(1488m, target);
        Assert.Equal([1m, 2m, 1488m], picker);
    }

    [Fact]
    public void A_source_that_listed_nothing_does_not_veto_the_others()
    {
        // A failed listing is an empty list, and must not be read as "this source agrees to nothing".
        var (_, target) = SourceComparePreviewService.PlanChapter(
            [Listing(1, 2, 3), Listing(), Listing(1, 2)], null);

        Assert.Equal(1m, target);
    }

    [Fact]
    public void Chapter_picker_is_capped_to_the_ends_of_a_long_catalogue()
    {
        var long1 = Listing([.. Enumerable.Range(1, 400).Select(n => (decimal)n)]);
        var long2 = Listing([.. Enumerable.Range(1, 400).Select(n => (decimal)n)]);

        var (common, target) = SourceComparePreviewService.PlanChapter([long1, long2], null);

        Assert.Equal(1m, target);
        Assert.Equal(30, common.Count);
        Assert.Equal(1m, common.First());
        Assert.Equal(400m, common.Last());
    }

    [Fact]
    public void Unnumbered_chapters_are_ignored_when_matching()
    {
        // One-shots and specials carry no number, so there is nothing to line up across sources.
        IReadOnlyList<SourceChapter> withSpecial =
            [new("fake", "s", "x", null, null, null, "Special", "en", null), Chapter(5)];

        var (_, target) = SourceComparePreviewService.PlanChapter([withSpecial, Listing(5, 6)], null);

        Assert.Equal(5m, target);
    }
}
