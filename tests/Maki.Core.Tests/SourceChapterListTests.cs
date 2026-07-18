using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class SourceChapterListTests
{
    private static SourceChapter Ch(
        decimal? number, int? volume = null, string language = "en", string id = "x",
        DateTime? released = null) =>
        new("test", "series-1", id, number?.ToString(), number, volume, null, language, released);

    [Fact]
    public void Orders_Ascending_By_Number()
    {
        var result = SourceChapterList.Normalize([Ch(3), Ch(1), Ch(2)]);

        Assert.Equal([1m, 2m, 3m], result.Select(c => c.Number));
    }

    [Fact]
    public void Dedupes_Same_Number_Keeping_First_By_Default()
    {
        var result = SourceChapterList.Normalize([Ch(1, id: "first"), Ch(1, id: "second")]);

        Assert.Equal("first", Assert.Single(result).SourceChapterId);
    }

    [Fact]
    public void Keeps_Same_Number_In_Different_Volumes()
    {
        // Volume is part of the key: sources disagree on volume assignment, and collapsing across
        // volumes here would silently drop a chapter.
        var result = SourceChapterList.Normalize([Ch(1, volume: 1), Ch(1, volume: 2)]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Keeps_Same_Number_In_Different_Languages()
    {
        var result = SourceChapterList.Normalize([Ch(1, language: "en"), Ch(1, language: "es")]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Preferred_Picks_The_Winner_Among_Duplicates()
    {
        // MangaDex's rule: earliest release wins among scanlation groups.
        var early = new DateTime(2024, 1, 1);
        var late = new DateTime(2025, 1, 1);
        var result = SourceChapterList.Normalize(
            [Ch(1, id: "late", released: late), Ch(1, id: "early", released: early)],
            g => g.OrderBy(c => c.ReleaseDate ?? DateTime.MaxValue).First());

        Assert.Equal("early", Assert.Single(result).SourceChapterId);
    }

    [Fact]
    public void Projecting_Overload_Picks_Using_Carried_Data()
    {
        // MangaFire's rule: official beats an unofficial rip of the same number.
        var items = new[] { (Chapter: Ch(1, id: "rip"), Official: false), (Chapter: Ch(1, id: "official"), Official: true) };

        var result = SourceChapterList.Normalize(
            items, i => i.Chapter, g => g.OrderByDescending(i => i.Official).First());

        Assert.Equal("official", Assert.Single(result).SourceChapterId);
    }

    [Fact]
    public void Null_Numbered_Chapters_Survive_And_Sort_First()
    {
        var result = SourceChapterList.Normalize([Ch(2), Ch(null, id: "oneshot")]);

        Assert.Equal(2, result.Count);
        Assert.Equal("oneshot", result[0].SourceChapterId);
    }

    [Fact]
    public void Empty_Input_Yields_Empty_List()
    {
        Assert.Empty(SourceChapterList.Normalize([]));
    }
}
