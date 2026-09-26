using Maki.Core.Sources;
using Maki.Sources.ManhwaWeb;

namespace Maki.Sources.Tests;

public class ManhwaWebSourceTests
{
    private static ManhwaWebSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static ManhwaWebSource WithSearch() =>
        SourceFor(new() { ["library?buscar="] = FakeHttpClientFactory.Fixture("manhwaweb-search.json") });

    private static ManhwaWebSource WithSeries() =>
        SourceFor(new() { ["manhwa/see/solo-leveling-ragnarok"] = FakeHttpClientFactory.Fixture("manhwaweb-series.json") });

    private static ManhwaWebSource WithRenamedSeries() =>
        SourceFor(new() { ["manhwa/see/comic-solo"] = FakeHttpClientFactory.Fixture("manhwaweb-series-renamed.json") });

    [Fact]
    public async Task Search_drops_novels()
    {
        var results = await WithSearch().SearchAsync("solo leveling");

        Assert.DoesNotContain(results, r => r.SourceSeriesId == "sololevelingragnarok_1717177581046");
        Assert.Equal(4, results.Count);
    }

    [Fact]
    public async Task Search_maps_hits_to_series_results()
    {
        var results = await WithSearch().SearchAsync("solo leveling");

        var hit = results.Single(r => r.SourceSeriesId == "solo-leveling-ragnarok_1783909089054");
        Assert.Equal("Solo Leveling: Ragnarok", hit.Title);
        Assert.Equal("https://manhwaweb.com/manhwa/solo-leveling-ragnarok_1783909089054", hit.Url);
        Assert.StartsWith("https://img2mw.xyz/", hit.CoverUrl);
    }

    [Fact]
    public async Task Search_stops_paging_once_next_is_false()
    {
        // The fixture's "next" is false. A page=1 or page=2 request would still match the
        // "library?buscar=" fixture key, so the only way to tell a stray extra request apart
        // is to count how many were made.
        var factory = new FakeHttpClientFactory(new()
        {
            ["library?buscar="] = FakeHttpClientFactory.Fixture("manhwaweb-search.json")
        });
        var source = new ManhwaWebSource(factory);

        await source.SearchAsync("solo leveling");

        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task GetSeries_reads_title_synopsis_status_and_cover()
    {
        var detail = await WithSeries().GetSeriesAsync("solo-leveling-ragnarok_1783909089054");

        Assert.Equal("solo-leveling-ragnarok_1783909089054", detail.SourceSeriesId);
        Assert.Equal("Solo Leveling: Ragnarok", detail.Title);
        Assert.Equal("https://manhwaweb.com/manhwa/solo-leveling-ragnarok_1783909089054", detail.Url);
        Assert.Equal("publicandose", detail.Status);
        Assert.False(string.IsNullOrEmpty(detail.Description));
        Assert.StartsWith("https://img2mw.xyz/", detail.CoverUrl);
    }

    [Fact]
    public async Task ListChapters_orders_ascending_and_keeps_fractional_numbers()
    {
        var chapters = await WithSeries().ListChaptersAsync("solo-leveling-ragnarok_1783909089054");

        Assert.Equal(70, chapters.Count);
        Assert.Equal(0.01m, chapters[0].Number);
        Assert.Equal(68.05m, chapters[^1].Number);
        Assert.All(chapters, c => Assert.Equal("es", c.Language));
        Assert.All(chapters, c => Assert.Null(c.Volume));
        Assert.All(chapters, c => Assert.NotNull(c.ReleaseDate));
    }

    [Fact]
    public async Task ListChapters_builds_ids_and_urls_on_the_series_own_id_when_it_matches_real_id()
    {
        var chapters = await WithSeries().ListChaptersAsync("solo-leveling-ragnarok_1783909089054");

        var first = chapters[0];
        Assert.Equal("solo-leveling-ragnarok_1783909089054-0.01_01", first.SourceChapterId);
        Assert.Equal("https://manhwaweb.com/leer/solo-leveling-ragnarok_1783909089054-0.01_01", first.Url);
    }

    [Fact]
    public async Task ListChapters_rewrites_ids_from_the_internal_id_to_real_id_when_they_differ()
    {
        var chapters = await WithRenamedSeries().ListChaptersAsync("comic-solo_leveling_1691379040764");

        Assert.Equal(202, chapters.Count);
        Assert.All(chapters, c => Assert.StartsWith("comic-solo_leveling_1691379040764-", c.SourceChapterId));
        Assert.All(chapters, c => Assert.StartsWith(
            "https://manhwaweb.com/leer/comic-solo_leveling_1691379040764-", c.Url ?? string.Empty));

        var five = chapters.Single(c => c.Number == 5m);
        Assert.Equal("comic-solo_leveling_1691379040764-5_01", five.SourceChapterId);
    }

    [Fact]
    public async Task ListChapters_keeps_the_raw_label_as_title_when_the_number_is_unparseable()
    {
        var source = SourceFor(new()
        {
            ["manhwa/see/test-series"] = FakeHttpClientFactory.Fixture("manhwaweb-series-oddchapter.json")
        });

        var chapters = await source.ListChaptersAsync("test-series_1");

        var numbered = chapters.Single(c => c.Number == 1m);
        Assert.Null(numbered.Title);

        var special = chapters.Single(c => c.Number is null);
        Assert.Equal("Especial", special.Title);
    }

    [Fact]
    public async Task GetPages_orders_pages_and_carries_the_referer()
    {
        var source = SourceFor(new()
        {
            ["chapters/see/"] = FakeHttpClientFactory.Fixture("manhwaweb-pages.json")
        });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "manhwaweb", "solo-leveling-ragnarok_1783909089054",
            "solo-leveling-ragnarok_1783909089054-1_01", "1", 1m, null, null, "es", null));

        Assert.Equal(25, pages.Pages.Count);
        Assert.Equal(
            "https://img2mw.xyz/manhwas/solo-leveling-ragnarok_1783909089054/chapter_1/ver_01/001.webp",
            pages.Pages[0].Url);
        Assert.All(pages.Pages, p => Assert.Equal("https://manhwaweb.com/", p.Headers!["Referer"]));
    }

    [Fact]
    public async Task GetPages_throws_on_the_plain_text_error_body_a_wrong_id_returns()
    {
        var source = SourceFor(new() { ["chapters/see/"] = "errorrgaarotosi" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPagesAsync(new SourceChapter(
            "manhwaweb", "comic-el-chetado_1691379040764",
            "comic-el-chetado_1691379040764-5_01", "5", 5m, null, null, "es", null)));
    }

    [Fact]
    public async Task GetPages_throws_locked_rather_than_returning_empty_when_img_is_empty()
    {
        // The site has no paid chapters, so an empty img[] is never "not unlocked yet" - it means
        // the chapter isn't actually up. This must never surface as a zero-page ChapterPages, or
        // the download pipeline writes an empty CBZ instead of retrying.
        var source = SourceFor(new()
        {
            ["chapters/see/"] = FakeHttpClientFactory.Fixture("manhwaweb-pages-empty.json")
        });

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(new SourceChapter(
            "manhwaweb", "solo-leveling-ragnarok_1783909089054",
            "solo-leveling-ragnarok_1783909089054-999_01", "999", 999m, null, null, "es", null)));
    }

    [Fact]
    public async Task GetPages_throws_locked_and_names_the_roto_flag_when_the_chapter_is_marked_broken()
    {
        var source = SourceFor(new()
        {
            ["chapters/see/"] = FakeHttpClientFactory.Fixture("manhwaweb-pages-broken.json")
        });

        var ex = await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(new SourceChapter(
            "manhwaweb", "solo-leveling-ragnarok_1783909089054",
            "solo-leveling-ragnarok_1783909089054-999_01", "999", 999m, null, null, "es", null)));

        Assert.Contains("roto=si", ex.Message);
    }
}
