using Maki.Core.Sources;
using Maki.Sources.TeamX;

namespace Maki.Sources.Tests;

public class TeamXSourceTests
{
    /// <summary>A Cloudflare interstitial or any other unexpected shape: no author-info-title h1 at all.</summary>
    private const string NotASeriesPage = "<!DOCTYPE html><html><body><p>Just a moment...</p></body></html>";

    /// <summary>
    /// A minimal series page with one locked and one unlocked chapter-card, and no ul.pagination
    /// so the walk never tries a second page.
    /// </summary>
    private const string PageWithLockedChapter = """
        <!DOCTYPE html><html><body>
        <div class="author-info-title"><h1>Test Series</h1></div>
        <div class="chapter-card" data-date="1700000000" data-number="5">
            <a href="https://olympustaff.com/series/TS/5" class="chapter-link"></a>
            <span class="status-badge locked"></span>
            <div class="chapter-title">الفصل 5</div>
        </div>
        <div class="chapter-card" data-date="1700000100" data-number="6">
            <a href="https://olympustaff.com/series/TS/6" class="chapter-link"></a>
            <div class="chapter-title">فصل مميز</div>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task Search_parses_cards_and_strips_the_thumbnail_prefix()
    {
        var source = new TeamXSource(new FakeHtmlFetcher(new()
        {
            ["/search?keyword="] = FakeHttpClientFactory.Fixture("teamx-search.html")
        }));

        var results = await source.SearchAsync("solo");

        Assert.Equal(8, results.Count);

        var soloLeveling = results.Single(r => r.SourceSeriesId == "SL");
        Assert.Equal("Solo Leveling", soloLeveling.Title);
        Assert.Equal("https://olympustaff.com/series/SL", soloLeveling.Url);
        Assert.Equal(
            "https://olympustaff.com/images/manga/b05147568d52f22c663c084172fc0dac.png",
            soloLeveling.CoverUrl);

        // Slugs are case-sensitive and must never be lower-cased.
        Assert.Contains(results, r => r.SourceSeriesId == "SMN");
    }

    [Fact]
    public async Task GetSeries_parses_title_cover_status_and_description()
    {
        var source = new TeamXSource(new FakeHtmlFetcher(new()
        {
            ["/series/SL"] = FakeHttpClientFactory.Fixture("teamx-series.html")
        }));

        var detail = await source.GetSeriesAsync("SL");

        Assert.Equal("SL", detail.SourceSeriesId);
        Assert.Equal("Solo Leveling", detail.Title);
        Assert.Equal("https://olympustaff.com/series/SL", detail.Url);
        Assert.Equal(
            "https://olympustaff.com/images/manga/b05147568d52f22c663c084172fc0dac.png",
            detail.CoverUrl);
        Assert.Equal("Completed", detail.Status);
        Assert.Contains("الباب", detail.Description);
    }

    [Fact]
    public async Task GetSeries_throws_on_a_page_that_does_not_look_like_a_series_page()
    {
        var source = new TeamXSource(new FakeHtmlFetcher(new()
        {
            ["/series/SL"] = NotASeriesPage
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetSeriesAsync("SL"));
        Assert.Contains("https://olympustaff.com/series/SL", ex.Message);
    }

    [Fact]
    public async Task ListChapters_walks_every_pagination_page_and_normalizes_ascending()
    {
        // Pages 2..6 all answer with the recorded last page (chapter 1 only); Normalize collapses
        // the five duplicate "chapter 1" rows into one, so the assertions below only care that the
        // walk actually reached every page the pager declared.
        var lastPage = FakeHttpClientFactory.Fixture("teamx-series-page6.html");
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["?page=2"] = lastPage,
            ["?page=3"] = lastPage,
            ["?page=4"] = lastPage,
            ["?page=5"] = lastPage,
            ["?page=6"] = lastPage,
            ["/series/SL"] = FakeHttpClientFactory.Fixture("teamx-series.html"),
        });

        var chapters = await new TeamXSource(fetcher).ListChaptersAsync("SL");

        Assert.Equal(41, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("ar", c.Language));
        Assert.Equal(1m, chapters[0].Number);
        Assert.Equal(200.5m, chapters[^1].Number);

        Assert.Contains(
            "https://olympustaff.com/series/SL?page=6", fetcher.Requested);
        Assert.Contains(
            "https://olympustaff.com/series/SL?page=2", fetcher.Requested);
    }

    [Fact]
    public async Task ListChapters_reads_number_date_and_drops_a_bare_number_title()
    {
        var lastPage = FakeHttpClientFactory.Fixture("teamx-series-page6.html");
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["?page=2"] = lastPage,
            ["?page=3"] = lastPage,
            ["?page=4"] = lastPage,
            ["?page=5"] = lastPage,
            ["?page=6"] = lastPage,
            ["/series/SL"] = FakeHttpClientFactory.Fixture("teamx-series.html"),
        });

        var chapters = await new TeamXSource(fetcher).ListChaptersAsync("SL");

        var one = chapters.Single(c => c.Number == 1m);
        // The site's own title is "الفصل رقم  01" (padded, double-spaced), a restatement of the
        // chapter number, so it must become null rather than an invented English literal.
        Assert.Null(one.Title);
        Assert.Equal("1", one.NumberRaw);
        Assert.Equal(new DateTime(2023, 1, 10, 20, 0, 0, DateTimeKind.Utc), one.ReleaseDate);
        Assert.Equal("https://olympustaff.com/series/SL/1", one.Url);

        var afterword = chapters.Single(c => c.Number == 200.5m);
        Assert.Equal("رسالة الخاتمة من الاستوديو", afterword.Title);

        var special = chapters.Single(c => c.Number == 200m);
        Assert.Equal("سولو 200: النهاية", special.Title);
    }

    [Fact]
    public async Task ListChapters_skips_cards_marked_locked()
    {
        var source = new TeamXSource(new FakeHtmlFetcher(new()
        {
            ["/series/TS"] = PageWithLockedChapter
        }));

        var chapters = await source.ListChaptersAsync("TS");

        var chapter = Assert.Single(chapters);
        Assert.Equal(6m, chapter.Number);
        Assert.Equal("فصل مميز", chapter.Title);
    }

    [Fact]
    public async Task ListChapters_throws_on_a_page_that_does_not_look_like_a_series_page()
    {
        var source = new TeamXSource(new FakeHtmlFetcher(new()
        {
            ["/series/SL"] = NotASeriesPage
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("SL"));
        Assert.Contains("https://olympustaff.com/series/SL", ex.Message);
    }

    [Fact]
    public async Task GetPages_returns_images_scoped_to_image_list_only()
    {
        var source = new TeamXSource(new FakeHtmlFetcher(new()
        {
            ["/series/SL/200"] = FakeHttpClientFactory.Fixture("teamx-chapter.html")
        }));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "teamx", "SL", "200", "200", 200m, null, null, "ar", null));

        Assert.Equal(13, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://olympustaff.com/uploads/", p.Url);
            Assert.Equal("https://olympustaff.com/", p.Headers!["Referer"]);
        });

        // The promo banner sits right after div.image_list in the recorded page and must never
        // be picked up by a page-wide image selector.
        Assert.DoesNotContain(pages.Pages, p => p.Url.Contains("i.ibb.co"));
    }

    [Fact]
    public async Task GetPages_throws_when_the_chapter_page_has_no_image_list()
    {
        var source = new TeamXSource(new FakeHtmlFetcher(new()
        {
            ["/series/SL/200"] = NotASeriesPage
        }));

        var chapter = new SourceChapter(
            "teamx", "SL", "200", "200", 200m, null, null, "ar", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPagesAsync(chapter));
        Assert.Contains("https://olympustaff.com/series/SL/200", ex.Message);
    }

    [Fact]
    public void ResolveSeriesIdFromUrl_accepts_series_and_rejects_chapter_and_query_urls()
    {
        var source = new TeamXSource(null!);

        Assert.Equal("SL", source.ResolveSeriesIdFromUrl(new Uri("https://olympustaff.com/series/SL")));
        Assert.Null(source.ResolveSeriesIdFromUrl(new Uri("https://olympustaff.com/series/SL/200")));
        Assert.Null(source.ResolveSeriesIdFromUrl(new Uri("https://olympustaff.com/search?keyword=solo")));
        Assert.Null(source.ResolveSeriesIdFromUrl(new Uri("https://example.com/series/SL")));
    }
}
