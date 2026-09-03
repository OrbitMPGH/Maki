using System.Globalization;
using Maki.Core.Entities;

namespace Maki.Core.Tests;

public class ChapterWantedTests
{
    private static decimal? Num(string? number) =>
        number is null ? null : decimal.Parse(number, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("10.5", true)]
    [InlineData("1.1", true)]
    [InlineData("10", false)]
    [InlineData("10.0", false)]
    [InlineData(null, false)] // one-shots are not specials
    public void IsSpecial(string? number, bool expected) =>
        Assert.Equal(expected, Chapter.IsSpecial(Num(number)));

    [Theory]
    // All ignores the specials setting: SeriesCreationService already downgrades All to MainOnly
    // when it's on, so honouring it here too would skip specials on a series set to All by hand.
    [InlineData(NewChapterMonitorMode.All, "10.5", false, true)]
    [InlineData(NewChapterMonitorMode.All, "10.5", true, true)]
    [InlineData(NewChapterMonitorMode.All, "10", true, true)]
    [InlineData(NewChapterMonitorMode.MainOnly, "10", false, true)]
    [InlineData(NewChapterMonitorMode.MainOnly, "10.5", false, false)]
    [InlineData(NewChapterMonitorMode.MainOnly, null, false, true)] // one-shots count as main
    // Smart can't be combined with MainOnly, so it reads the setting directly. It used to return
    // false for everything, which is what stopped new chapters ever counting toward the total.
    [InlineData(NewChapterMonitorMode.Smart, "10", false, true)]
    [InlineData(NewChapterMonitorMode.Smart, "10.5", false, true)]
    [InlineData(NewChapterMonitorMode.Smart, "10.5", true, false)]
    [InlineData(NewChapterMonitorMode.Smart, "10", true, true)]
    [InlineData(NewChapterMonitorMode.None, "10", false, false)]
    [InlineData(NewChapterMonitorMode.None, null, false, false)]
    public void WantedUnder(NewChapterMonitorMode mode, string? number, bool skipSpecials, bool expected) =>
        Assert.Equal(expected, Chapter.WantedUnder(mode, Num(number), skipSpecials));

    private static Chapter Ch(int id, decimal? number, bool wanted = true, int? fileId = null) =>
        new() { Id = id, Number = number, Language = "en", Wanted = wanted, ChapterFileId = fileId };

    [Fact]
    public void NextWanted_takes_the_lowest_numbered_chapters_not_the_first_rows()
    {
        // Insertion order deliberately scrambled: sources list newest-first, and this used to be a
        // bare .Take() over DB order, so "the next 10" was whatever the site happened to list first.
        var chapters = new List<Chapter> { Ch(1, 40m), Ch(2, 10m), Ch(3, 30m), Ch(4, 20m) };

        Assert.Equal([2, 4], Chapter.NextWanted(chapters, 2));
    }

    [Fact]
    public void NextWanted_skips_downloaded_and_unwanted_chapters()
    {
        var chapters = new List<Chapter>
        {
            Ch(1, 1m, fileId: 100),
            Ch(2, 2m, wanted: false),
            Ch(3, 3m),
            Ch(4, 4m),
        };

        Assert.Equal([3, 4], Chapter.NextWanted(chapters, 10));
    }

    [Fact]
    public void NextWanted_sorts_one_shots_last()
    {
        var chapters = new List<Chapter> { Ch(1, null), Ch(2, 2m) };

        Assert.Equal([2, 1], Chapter.NextWanted(chapters, 10));
    }

    [Fact]
    public void NextWanted_returns_everything_when_count_exceeds_what_is_missing()
    {
        var chapters = new List<Chapter> { Ch(1, 1m), Ch(2, 2m) };

        Assert.Equal([1, 2], Chapter.NextWanted(chapters, 50));
    }
}
