using Mangarr.Core.Entities;

namespace Mangarr.Core.Tests;

public class SeriesWebLinksTests
{
    [Fact]
    public void AllIds()
    {
        var series = new Series
        {
            MangaBakaId = 377,
            AniListId = 30013,
            MalId = 21,
            MangaUpdatesId = "d2ttegm",
            MangaDexUuid = "a1c7c817-4e59-43b7-9365-09675a149a6f"
        };

        Assert.Equal(
        [
            "https://mangabaka.org/377",
            "https://anilist.co/manga/30013",
            "https://myanimelist.net/manga/21",
            "https://www.mangaupdates.com/series/d2ttegm",
            "https://mangadex.org/title/a1c7c817-4e59-43b7-9365-09675a149a6f"
        ], SeriesWebLinks.For(series));

        Assert.Equal(
            "https://mangabaka.org/377,https://anilist.co/manga/30013,https://myanimelist.net/manga/21," +
            "https://www.mangaupdates.com/series/d2ttegm,https://mangadex.org/title/a1c7c817-4e59-43b7-9365-09675a149a6f",
            SeriesWebLinks.Joined(series));
    }

    [Fact]
    public void NoIds()
    {
        var series = new Series();
        Assert.Empty(SeriesWebLinks.For(series));
        Assert.Null(SeriesWebLinks.Joined(series));
    }

    [Fact]
    public void BlankStringIdsAreSkipped()
    {
        var series = new Series { MangaBakaId = 1, MangaUpdatesId = " ", MangaDexUuid = "" };
        Assert.Equal(["https://mangabaka.org/1"], SeriesWebLinks.For(series));
    }
}
