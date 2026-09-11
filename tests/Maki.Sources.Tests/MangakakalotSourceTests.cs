using Maki.Core.Sources;
using Maki.Sources.Mangakakalot;

namespace Maki.Sources.Tests;

public class MangakakalotSourceTests
{
    /// <summary>A last page: the walk in ListChaptersAsync stops on has_more=false, not on an empty list.</summary>
    private const string EmptyLastPage =
        """{"success":true,"data":{"chapters":[],"pagination":{"total":658,"limit":100,"offset":100,"has_more":false}}}""";

    [Theory]
    [InlineData("Tower of God", "tower_of_god")]
    [InlineData("  One   Piece  ", "one_piece")]
    [InlineData("Kaguya-sama: Love Is War", "kaguya_sama_love_is_war")]
    [InlineData("Re:Zero", "re_zero")]
    public void SearchKeyword_collapses_punctuation_to_underscores(string title, string expected) =>
        Assert.Equal(expected, MangakakalotSource.SearchKeyword(title));

    [Fact]
    public async Task Search_parses_results()
    {
        var source = new MangakakalotSource(new FakeHtmlFetcher(new()
        {
            ["/search/story/"] = FakeHttpClientFactory.Fixture("mangakakalot-search.html")
        }));

        var results = await source.SearchAsync("tower of god");

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("tower-of-god", first.SourceSeriesId);
        Assert.Equal("Tower Of God", first.Title);
        Assert.Equal("https://www.mangakakalot.gg/manga/tower-of-god", first.Url);
        Assert.StartsWith("https://", first.CoverUrl);

        // Every hit is a slug, never the full href the markup carries.
        Assert.All(results, r => Assert.DoesNotContain('/', r.SourceSeriesId));
    }

    [Fact]
    public async Task Search_hits_the_keyword_path()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["/search/story/"] = FakeHttpClientFactory.Fixture("mangakakalot-search.html")
        });

        await new MangakakalotSource(fetcher).SearchAsync("Tower of God");

        Assert.Equal("https://www.mangakakalot.gg/search/story/tower_of_god", Assert.Single(fetcher.Requested));
    }

    [Fact]
    public async Task GetSeries_parses_detail()
    {
        var source = new MangakakalotSource(new FakeHtmlFetcher(new()
        {
            ["/manga/"] = FakeHttpClientFactory.Fixture("mangakakalot-series.html")
        }));

        var detail = await source.GetSeriesAsync("tower-of-god");

        Assert.Equal("tower-of-god", detail.SourceSeriesId);
        Assert.Equal("Tower Of God", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal("https://imgs-2.2xstorage.com/thumb/tower-of-god.webp", detail.CoverUrl);
        Assert.Contains("Twenty-Fifth Baam", detail.Description);

        // The block starts with an "{title} summary:" heading and ends with the site's own
        // "+ Other Manga" cross-promo links; neither is synopsis.
        Assert.DoesNotContain("summary:", detail.Description);
        Assert.DoesNotContain("+ Berserk", detail.Description);
    }

    [Fact]
    public async Task ListChapters_parses_the_json_api_and_orders_ascending()
    {
        var source = new MangakakalotSource(new FakeHtmlFetcher(new()
        {
            ["offset=0&"] = FakeHttpClientFactory.Fixture("mangakakalot-chapters.json"),
            ["offset=100&"] = EmptyLastPage
        }));

        var chapters = await source.ListChaptersAsync("tower-of-god");

        Assert.Equal(100, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.Equal(553m, chapters[0].Number);
        Assert.Equal(652m, chapters[^1].Number);

        var last = chapters[^1];
        Assert.Equal("chapter-652", last.SourceChapterId);
        Assert.Equal("Chapter 652", last.NumberRaw);
        Assert.Equal("https://www.mangakakalot.gg/manga/tower-of-god/chapter-652", last.Url);
        Assert.Equal(new DateTime(2025, 6, 19, 5, 51, 45, DateTimeKind.Utc), last.ReleaseDate);
    }

    [Fact]
    public async Task ListChapters_walks_offsets_until_has_more_is_false()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["offset=0&"] = FakeHttpClientFactory.Fixture("mangakakalot-chapters.json"),
            ["offset=100&"] = EmptyLastPage
        });

        await new MangakakalotSource(fetcher).ListChaptersAsync("tower-of-god");

        Assert.Equal(
        [
            "https://www.mangakakalot.gg/api/manga/tower-of-god/chapters?offset=0&limit=100",
            "https://www.mangakakalot.gg/api/manga/tower-of-god/chapters?offset=100&limit=100"
        ], fetcher.Requested);
    }

    [Fact]
    public async Task GetPages_returns_urls_with_referer()
    {
        var source = new MangakakalotSource(new FakeHtmlFetcher(new()
        {
            ["/chapter-0"] = FakeHttpClientFactory.Fixture("mangakakalot-chapter.html")
        }));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "mangakakalot", "tower-of-god", "chapter-0", "Chapter 0", 0, null, null, "en", null));

        Assert.NotEmpty(pages.Pages);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://", p.Url);
            // The CDN 403s a page request that arrives without one.
            Assert.Equal("https://www.mangakakalot.gg/", p.Headers!["Referer"]);
        });
    }
}
