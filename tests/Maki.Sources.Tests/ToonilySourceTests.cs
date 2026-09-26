using Maki.Core.Sources;
using Maki.Sources.Toonily;

namespace Maki.Sources.Tests;

public class ToonilySourceTests
{
    [Theory]
    [InlineData("Secret Class", "secret-class")]
    [InlineData("  Solo   Leveling  ", "solo-leveling")]
    [InlineData("Re:Zero", "re-zero")]
    public void SearchSlug_collapses_punctuation_to_hyphens(string title, string expected) =>
        Assert.Equal(expected, ToonilySource.SearchSlug(title));

    [Fact]
    public async Task Search_parses_the_admin_ajax_response()
    {
        var source = new ToonilySource(
            new FakeHtmlFetcher(new()),
            new FakeHttpClientFactory(new()
            {
                ["admin-ajax"] = FakeHttpClientFactory.Fixture("toonily-search.html")
            }));

        var results = await source.SearchAsync("secret class");

        Assert.True(results.Count >= 10);
        var first = results[0];
        Assert.Equal("secret-class-38c3e37a", first.SourceSeriesId);
        Assert.Equal("Secret Class", first.Title);
        Assert.Equal("https://toonily.com/serie/secret-class-38c3e37a/", first.Url);
        Assert.StartsWith("https://", first.CoverUrl);

        // Every hit is a slug, never the full href the markup carries.
        Assert.All(results, r => Assert.DoesNotContain("https://", r.SourceSeriesId));
    }

    [Fact]
    public async Task Search_falls_back_to_the_get_search_when_the_post_fails()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["/search/"] = FakeHttpClientFactory.Fixture("toonily-search-get.html")
        });

        // No "admin-ajax" fixture registered, so the POST 404s and the fallback runs.
        var source = new ToonilySource(fetcher, new FakeHttpClientFactory(new()));

        var results = await source.SearchAsync("secret class");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.SourceSeriesId == "secret-class-38c3e37a");
        Assert.Equal("https://toonily.com/search/secret-class", Assert.Single(fetcher.Requested));
    }

    [Fact]
    public async Task GetSeries_parses_detail()
    {
        var source = new ToonilySource(
            new FakeHtmlFetcher(new()
            {
                ["/serie/"] = FakeHttpClientFactory.Fixture("toonily-series.html")
            }),
            new FakeHttpClientFactory(new()));

        var detail = await source.GetSeriesAsync("secret-class-38c3e37a");

        Assert.Equal("secret-class-38c3e37a", detail.SourceSeriesId);
        Assert.Equal("Secret Class", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.StartsWith("https://static.tnlycdn.com", detail.CoverUrl);
        Assert.Contains("Dae Ho", detail.Description);

        // First paragraph only; the SEO filler paragraphs after it aren't synopsis.
        Assert.DoesNotContain("commonly searched", detail.Description);
    }

    [Fact]
    public async Task ListChapters_parses_numbers_and_language_and_orders_ascending()
    {
        var source = new ToonilySource(
            new FakeHtmlFetcher(new()
            {
                ["/serie/"] = FakeHttpClientFactory.Fixture("toonily-series.html")
            }),
            new FakeHttpClientFactory(new()));

        var chapters = await source.ListChaptersAsync("secret-class-38c3e37a");

        Assert.True(chapters.Count >= 240);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.Equal(1m, chapters[0].Number);
        Assert.Equal(242m, chapters[^1].Number);
        Assert.Contains(chapters, c => c.Number == 129.5m);

        var last = chapters[^1];
        Assert.Equal("chapter-242", last.SourceChapterId);
        Assert.Equal("https://toonily.com/serie/secret-class-38c3e37a/chapter-242/", last.Url);
        Assert.Equal(new DateTime(2024, 12, 9), last.ReleaseDate);
    }

    [Fact]
    public async Task GetPages_returns_urls_with_referer_and_drops_the_promo_image()
    {
        var source = new ToonilySource(
            new FakeHtmlFetcher(new()
            {
                ["/chapter-242"] = FakeHttpClientFactory.Fixture("toonily-chapter.html")
            }),
            new FakeHttpClientFactory(new()));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "toonily", "secret-class-38c3e37a", "chapter-242", "Chapter 242", 242, null, null, "en", null));

        // 36 page-break divs in the fixture, 35 real pages once the Discord promo tile is dropped.
        Assert.Equal(35, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://", p.Url);
            Assert.DoesNotContain("/wp-content/", p.Url);
            Assert.Equal("https://toonily.com/", p.Headers!["Referer"]);
        });
    }
}
