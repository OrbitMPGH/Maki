using Maki.Core.Sources;
using Maki.Sources.Olympus;

namespace Maki.Sources.Tests;

public class OlympusSourceTests
{
    private const string SeriesUrl = "api/series/10-05-2025-nivel-solo5324?type=comic";
    private const string ChaptersPage1 = "chapters?page=1&direction=desc&type=comic";
    private const string ChaptersPage5 = "chapters?page=5&direction=desc&type=comic";

    private static Dictionary<string, string> HappyPathFixtures() => new()
    {
        ["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json"),
        [SeriesUrl] = FakeHttpClientFactory.Fixture("olympus-series.json"),
        [ChaptersPage1] = FakeHttpClientFactory.Fixture("olympus-chapters-1.json"),
        // Pages 2 to 4 can repeat page 1's body; the test asserts the walk stops at last_page (5).
        ["chapters?page=2&direction=desc&type=comic"] = FakeHttpClientFactory.Fixture("olympus-chapters-1.json"),
        ["chapters?page=3&direction=desc&type=comic"] = FakeHttpClientFactory.Fixture("olympus-chapters-1.json"),
        ["chapters?page=4&direction=desc&type=comic"] = FakeHttpClientFactory.Fixture("olympus-chapters-1.json"),
        [ChaptersPage5] = FakeHttpClientFactory.Fixture("olympus-chapters-5.json"),
        // Matches whichever slug GetPagesAsync ends up using: the real one once the catalog is
        // warm, "comic-x" while cold. The dedicated real-vs-placeholder tests pin down which is which.
        ["api/capitulo/"] = FakeHttpClientFactory.Fixture("olympus-pages.json"),
    };

    [Fact]
    public async Task Search_matches_the_catalog_by_title()
    {
        var source = new OlympusSource(new FakeHtmlFetcher(HappyPathFixtures()));

        var results = await source.SearchAsync("Subo de nivel solo");

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("10", first.SourceSeriesId);
        Assert.Equal("Subo de nivel solo", first.Title);
        Assert.Equal("https://olympusxyz.com/series/comic-10-05-2025-nivel-solo5324", first.Url);
        Assert.StartsWith("https://", first.CoverUrl);
    }

    [Fact]
    public async Task Search_excludes_novels()
    {
        var source = new OlympusSource(new FakeHtmlFetcher(HappyPathFixtures()));

        // "Novela" titles are filtered out at catalog-fetch time (type == "novel"); searching for
        // one of their exact names should come back empty.
        var results = await source.SearchAsync("Muchas Torres (Novela)");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSeries_parses_detail_by_numeric_id()
    {
        var source = new OlympusSource(new FakeHtmlFetcher(HappyPathFixtures()));

        var detail = await source.GetSeriesAsync("10");

        Assert.Equal("10", detail.SourceSeriesId);
        Assert.Equal("Subo de nivel solo", detail.Title);
        Assert.Equal("Finalizado", detail.Status);
        Assert.Equal("https://olympusxyz.com/series/comic-10-05-2025-nivel-solo5324", detail.Url);
        Assert.Contains("Hanyeol", detail.Description);
        Assert.EndsWith("-xl.webp", detail.CoverUrl);
    }

    [Fact]
    public async Task GetSeries_throws_for_an_id_not_in_the_catalog()
    {
        var source = new OlympusSource(new FakeHtmlFetcher(HappyPathFixtures()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetSeriesAsync("999999999"));
    }

    [Fact]
    public async Task GetSeries_retries_once_on_error_then_gives_up()
    {
        // A "{"error":true,...}" body (both the 400 a numeric id gets and the 404 a rotated slug
        // gets come back shaped this way, since ChallengeAwareFetcher never surfaces the origin
        // status code to the source) should force exactly one catalog refresh and retry before
        // the call gives up, never looping forever.
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json"),
            [SeriesUrl] = FakeHttpClientFactory.Fixture("olympus-series-404.json"),
        });
        var source = new OlympusSource(fetcher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetSeriesAsync("10"));

        Assert.Equal(2, fetcher.Requested.Count(u => u.Contains("api/series/list")));
        Assert.Equal(2, fetcher.Requested.Count(u => u.Contains(SeriesUrl)));
    }

    [Fact]
    public async Task ListChapters_retries_once_on_error_then_gives_up()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json"),
            [ChaptersPage1] = FakeHttpClientFactory.Fixture("olympus-series-404.json"),
        });
        var source = new OlympusSource(fetcher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("10"));

        Assert.Equal(2, fetcher.Requested.Count(u => u.Contains("api/series/list")));
        Assert.Equal(2, fetcher.Requested.Count(u => u.Contains(ChaptersPage1)));
    }

    [Fact]
    public async Task ListChapters_walks_every_page_and_parses_numbers_and_language()
    {
        var source = new OlympusSource(new FakeHtmlFetcher(HappyPathFixtures()));

        var chapters = await source.ListChaptersAsync("10");

        // Pages 2-4 in this fixture set repeat page 1's 40 rows verbatim (see HappyPathFixtures),
        // so Normalize's (Number, Volume, Language) dedupe collapses them to one copy: page 1's
        // 40 unique chapters plus page 5's 26 (which don't overlap page 1's numbers) is 66. The
        // live site's real count (186) is asserted only by the live harness, not here.
        Assert.Equal(66, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("es", c.Language));
        Assert.All(chapters, c => Assert.Null(c.Volume));
        Assert.All(chapters, c => Assert.Null(c.Title));
        Assert.Contains(chapters, c => c.Number == 183.05m);
        Assert.Contains(chapters, c => c.Number == 160.01m);
        Assert.Contains(chapters, c => c.Number == 1m);
        Assert.DoesNotContain(chapters, c => c.Number is null);

        // Ascending order, per SourceChapterList.Normalize.
        Assert.Equal(1m, chapters[0].Number);
        Assert.Equal(183.05m, chapters[^1].Number);
    }

    [Fact]
    public async Task ListChapters_stops_at_last_page()
    {
        var fetcher = new FakeHtmlFetcher(HappyPathFixtures());

        await new OlympusSource(fetcher).ListChaptersAsync("10");

        Assert.Equal(5, fetcher.Requested.Count(u => u.Contains("/chapters?page=")));
    }

    [Fact]
    public async Task GetPages_returns_the_page_list_with_referer()
    {
        var source = new OlympusSource(new FakeHtmlFetcher(HappyPathFixtures()));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "olympus", "10", "114575", "183.05", 183.05m, null, null, "es", null));

        var page = Assert.Single(pages.Pages);
        Assert.Equal("https://media.imagesolymp.xyz/comics/10/114575/c-10-1.webp", page.Url);
        Assert.Equal("https://olympusxyz.com/", page.Headers!["Referer"]);
    }

    [Fact]
    public async Task GetPages_uses_the_real_slug_once_the_catalog_knows_it()
    {
        var fetcher = new FakeHtmlFetcher(HappyPathFixtures());
        var source = new OlympusSource(fetcher);

        // Forces the id-to-slug map to hold the real slug for "10" before fetching pages.
        await source.GetSeriesAsync("10");

        await source.GetPagesAsync(new SourceChapter(
            "olympus", "10", "114575", "183.05", 183.05m, null, null, "es", null));

        Assert.Contains(fetcher.Requested, u => u.Contains("api/capitulo/comic-10-05-2025-nivel-solo5324/114575"));
    }

    [Fact]
    public async Task GetPages_falls_back_to_a_placeholder_slug_while_the_catalog_is_cold()
    {
        // No fixture for the catalog list: the constructor's warm-up fetch fails and is swallowed,
        // so the id-to-slug map stays empty and this must not depend on it.
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["api/capitulo/"] = FakeHttpClientFactory.Fixture("olympus-pages.json"),
        });
        var source = new OlympusSource(fetcher);

        await source.GetPagesAsync(new SourceChapter(
            "olympus", "10", "114575", "183.05", 183.05m, null, null, "es", null));

        Assert.Contains(fetcher.Requested, u => u.Contains("api/capitulo/comic-x/114575"));
    }

    [Fact]
    public async Task GetPages_throws_locked_when_there_are_no_pages()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["api/capitulo/"] = """{"chapter":{"id":1,"name":"1","pages":[]},"series_id":10}""",
        });
        var source = new OlympusSource(fetcher);

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(
            new SourceChapter("olympus", "10", "1", "1", 1m, null, null, "es", null)));
    }

    [Fact]
    public async Task GetJson_unwraps_a_FlareSolverr_pre_tag()
    {
        // FlareSolverr returns JSON wrapped like a browser's raw-JSON viewer: <html><body><pre>...</pre></body></html>.
        var wrapped = $"<html><head></head><body><pre>{FakeHttpClientFactory.Fixture("olympus-series.json")}</pre></body></html>";
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json"),
            [SeriesUrl] = wrapped,
        });
        var source = new OlympusSource(fetcher);

        var detail = await source.GetSeriesAsync("10");

        Assert.Equal("Subo de nivel solo", detail.Title);
    }
}
