using Maki.Core.Sources;
using Maki.Sources.MangaWorld;

namespace Maki.Sources.Tests;

public class MangaWorldSourceTests
{
    [Fact]
    public async Task Search_parses_results()
    {
        var source = new MangaWorldSource(new FakeHtmlFetcher(new()
        {
            ["/archive?keyword="] = FakeHttpClientFactory.Fixture("mangaworld-search.html")
        }));

        var results = await source.SearchAsync("one piece");

        Assert.NotEmpty(results);
        var onePiece = Assert.Single(results, r => r.SourceSeriesId == "1708/one-piece");
        Assert.Equal("One Piece", onePiece.Title);
        Assert.Equal("https://www.mangaworld.mx/manga/1708/one-piece", onePiece.Url);
        Assert.StartsWith("https://cdn.mangaworld.mx/", onePiece.CoverUrl);

        var oneshot = Assert.Single(results, r => r.SourceSeriesId == "3294/nami-vs-kalifa");
        Assert.Equal("Nami VS Kalifa", oneshot.Title);
    }

    [Fact]
    public async Task GetSeries_parses_detail()
    {
        var source = new MangaWorldSource(new FakeHtmlFetcher(new()
        {
            ["/manga/1708/one-piece"] = FakeHttpClientFactory.Fixture("mangaworld-series.html")
        }));

        var detail = await source.GetSeriesAsync("1708/one-piece");

        Assert.Equal("1708/one-piece", detail.SourceSeriesId);
        Assert.Equal("One Piece", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal(
            "https://cdn.mangaworld.mx/mangas/5fa0c9e2c9f2201ee55d3bd4.jpg?1790359543495", detail.CoverUrl);
        Assert.Contains("Monkey D. Rufy", detail.Description);
    }

    [Fact]
    public async Task ListChapters_parses_volume_grouped_numbers_and_orders_ascending()
    {
        var source = new MangaWorldSource(new FakeHtmlFetcher(new()
        {
            ["/manga/1708/one-piece"] = FakeHttpClientFactory.Fixture("mangaworld-series.html")
        }));

        var chapters = await source.ListChaptersAsync("1708/one-piece");

        Assert.Equal(39, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("it", c.Language));
        Assert.All(chapters, c => Assert.Null(c.Title));

        // Site lists newest first; Normalize orders ascending.
        Assert.Equal(1156m, chapters[0].Number);
        Assert.Equal(1194m, chapters[^1].Number);

        var last = chapters[^1];
        Assert.Equal(116, last.Volume);
        Assert.Equal("6ab6b1f9c1f8362c329080c7", last.SourceChapterId);
        Assert.Equal("Capitolo 1194", last.NumberRaw);
        Assert.Equal(
            "https://www.mangaworld.mx/manga/1708/one-piece/read/6ab6b1f9c1f8362c329080c7", last.Url);
        Assert.Equal(new DateTime(2026, 9, 25), last.ReleaseDate);
    }

    [Fact]
    public async Task ListChapters_flat_list_gives_null_number_chapter_a_title()
    {
        var source = new MangaWorldSource(new FakeHtmlFetcher(new()
        {
            ["/manga/3294/nami-vs-kalifa"] = FakeHttpClientFactory.Fixture("mangaworld-series-flat.html")
        }));

        var chapters = await source.ListChaptersAsync("3294/nami-vs-kalifa");

        var chapter = Assert.Single(chapters);
        Assert.Null(chapter.Number);
        Assert.Null(chapter.Volume);
        // Null-number chapters need a non-null title so ChapterIdentity can dedupe by it.
        Assert.Equal("Oneshot", chapter.Title);
        Assert.Equal("649af99e8e54383f7f49f1ff", chapter.SourceChapterId);
        Assert.Equal(new DateTime(2023, 6, 27), chapter.ReleaseDate);
    }

    [Fact]
    public async Task GetPages_returns_urls_with_referer()
    {
        var source = new MangaWorldSource(new FakeHtmlFetcher(new()
        {
            ["/read/6ab6b1f9c1f8362c329080c7"] = FakeHttpClientFactory.Fixture("mangaworld-chapter.html")
        }));

        var chapter = new SourceChapter(
            "mangaworld", "1708/one-piece", "6ab6b1f9c1f8362c329080c7", "Capitolo 1194", 1194m, 116,
            null, "it", null);

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(12, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://cdn.mangaworld.mx/", p.Url);
            Assert.Equal("https://www.mangaworld.mx/", p.Headers!["Referer"]);
        });
    }

    [Fact]
    public async Task GetPages_throws_locked_when_the_page_list_is_empty()
    {
        var source = new MangaWorldSource(new FakeHtmlFetcher(new()
        {
            ["/read/"] = "<html><body><div id=page></div></body></html>"
        }));

        var chapter = new SourceChapter(
            "mangaworld", "1708/one-piece", "deadbeef", "Capitolo 1", 1m, null, null, "it", null);

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(chapter));
    }

    [Fact]
    public async Task ListChapters_throws_on_the_cookie_interstitial_instead_of_reading_no_chapters()
    {
        var source = new MangaWorldSource(new FakeHtmlFetcher(new()
        {
            ["/manga/"] = FakeHttpClientFactory.Fixture("mangaworld-cookie-shell.html")
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("1708/one-piece"));
    }

    [Theory]
    [InlineData("Capitolo 1194", "1194")]
    [InlineData("Capitolo 1023\t", "1023")]
    public void CapitoloRegex_extracts_the_bare_number(string raw, string expected)
    {
        // Regression guard for the source-local pre-extraction 00-GENERAL calls out as a deviation:
        // ChapterNumberParser only knows "ch"/"chapter", never the Italian "Capitolo" label.
        var match = System.Text.RegularExpressions.Regex.Match(
            raw, @"capitolo\s*(\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(match.Success);
        Assert.Equal(expected, match.Groups[1].Value);
    }
}
