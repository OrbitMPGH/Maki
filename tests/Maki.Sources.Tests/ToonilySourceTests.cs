using Maki.Core.Http;
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
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["admin-ajax"] = FakeHttpClientFactory.Fixture("toonily-search.html")
        });

        var source = new ToonilySource(fetcher);
        var results = await source.SearchAsync("secret class");

        Assert.True(results.Count >= 10);
        var first = results[0];
        Assert.Equal("secret-class-38c3e37a", first.SourceSeriesId);
        Assert.Equal("Secret Class", first.Title);
        Assert.Equal("https://toonily.com/serie/secret-class-38c3e37a/", first.Url);
        Assert.StartsWith("https://", first.CoverUrl);

        // Every hit is a slug, never the full href the markup carries.
        Assert.All(results, r => Assert.DoesNotContain("https://", r.SourceSeriesId));

        // The POST carries a form body and the mature-titles cookie, so adult titles show up
        // on either the direct request or FlareSolverr's browser (both accept cookies).
        var sent = Assert.Single(fetcher.Requests);
        Assert.Equal("https://toonily.com/wp-admin/admin-ajax.php", sent.Url);
        Assert.NotNull(sent.FormBody);
        Assert.Contains("action=madara_load_more", sent.FormBody);
        Assert.Contains("vars%5Bs%5D=secret", sent.FormBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1", sent.Cookies!["toonily-mature"]);
    }

    [Fact]
    public async Task Search_falls_back_to_the_get_search_when_the_post_times_out()
    {
        var fetcher = new TimingOutPostFetcher(new FakeHtmlFetcher(new()
        {
            ["/search/"] = FakeHttpClientFactory.Fixture("toonily-search-get.html")
        }));

        var source = new ToonilySource(fetcher);
        var results = await source.SearchAsync("secret class");

        Assert.Contains(results, r => r.SourceSeriesId == "secret-class-38c3e37a");
    }

    [Fact]
    public async Task Search_does_not_fall_back_when_the_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var inner = new FakeHtmlFetcher(new()
        {
            ["/search/"] = FakeHttpClientFactory.Fixture("toonily-search-get.html")
        });

        var source = new ToonilySource(new TimingOutPostFetcher(inner));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync("secret class", cts.Token));
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Search_falls_back_to_the_get_search_when_the_post_fails()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["/search/"] = FakeHttpClientFactory.Fixture("toonily-search-get.html")
        });

        // No "admin-ajax" fixture registered, so the POST throws and the fallback runs.
        var source = new ToonilySource(fetcher);
        var results = await source.SearchAsync("secret class");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.SourceSeriesId == "secret-class-38c3e37a");

        Assert.Equal(2, fetcher.Requests.Count);
        var fallback = fetcher.Requests[1];
        Assert.Equal("https://toonily.com/search/secret-class", fallback.Url);
        Assert.Equal("1", fallback.Cookies!["toonily-mature"]);
    }

    [Fact]
    public async Task GetSeries_parses_detail()
    {
        var source = new ToonilySource(
            new FakeHtmlFetcher(new()
            {
                ["/serie/"] = FakeHttpClientFactory.Fixture("toonily-series.html")
            }));

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
            }));

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
            }));

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

    [Fact]
    public async Task GetPages_throws_locked_when_every_image_is_filtered_out()
    {
        const string html = """
            <html><body>
            <div class="reading-content">
              <div class="page-break no-gaps">
                <img id="image-999" src="https://toonily.com/wp-content/assets/999.png" alt="Toonily Discord Server">
              </div>
            </div>
            </body></html>
            """;

        var source = new ToonilySource(
            new FakeHtmlFetcher(new() { ["/chapter-1"] = html }));

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(new SourceChapter(
            "toonily", "secret-class-38c3e37a", "chapter-1", "Chapter 1", 1, null, null, "en", null)));
    }

    [Fact]
    public async Task ListChapters_gives_unnumbered_entries_distinct_titles_before_the_alias_collapse()
    {
        // Two unparseable chapter names with no volume, so ChapterNumberParser gives both a null
        // Number: their only remaining identity before Normalize collapses them is Title.
        const string html = """
            <html><body>
            <ul class="main version-chap no-volumn">
              <li class="wp-manga-chapter">
                <a href="https://toonily.com/serie/secret-class-38c3e37a/one-shot-extra">One-Shot Extra</a>
              </li>
              <li class="wp-manga-chapter">
                <a href="https://toonily.com/serie/secret-class-38c3e37a/special-omake">Special Omake</a>
              </li>
            </ul>
            </body></html>
            """;

        var source = new ToonilySource(
            new FakeHtmlFetcher(new() { ["/serie/"] = html }));

        var chapters = await source.ListChaptersAsync("secret-class-38c3e37a");

        // Normalize dedupes by (Number, Volume, Language) alone, so these two unnumbered entries
        // collapse into one row today (a known Core limitation) — what this asserts is that
        // whichever one survives carries a real Title rather than null.
        var survivor = Assert.Single(chapters);
        Assert.True(survivor.Title is "One-Shot Extra" or "Special Omake");
    }

    /// <summary>Throws what HttpClient throws when its Timeout fires, for every POST.</summary>
    private sealed class TimingOutPostFetcher(IHtmlFetcher inner) : IHtmlFetcher
    {
        public Task<string> GetHtmlAsync(string url, CancellationToken ct = default) =>
            FetchAsync(new HtmlFetchRequest(url), ct);

        public Task<string> FetchAsync(HtmlFetchRequest request, CancellationToken ct = default) =>
            request.FormBody is not null
                ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                    new TimeoutException())
                : inner.FetchAsync(request, ct);
    }
}
