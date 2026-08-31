using Maki.Core.Sources;
using Maki.Sources.Atsumaru;

namespace Maki.Sources.Tests;

public class AtsumaruSourceTests
{
    private static AtsumaruSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static AtsumaruSource WithSearch() =>
        SourceFor(new() { ["search/manga"] = FakeHttpClientFactory.Fixture("atsumaru-search.json") });

    private static AtsumaruSource WithSeries() =>
        SourceFor(new()
        {
            ["manga/page"] = FakeHttpClientFactory.Fixture("atsumaru-page.json"),
            ["manga/info"] = FakeHttpClientFactory.Fixture("atsumaru-info.json")
        });

    [Fact]
    public async Task Search_maps_hits_to_series_results()
    {
        var results = await WithSearch().SearchAsync("ippo");

        Assert.Equal(3, results.Count);
        Assert.Equal("94bKW", results[0].SourceSeriesId);
        Assert.Equal("Hajime no Ippo: Fighting Spirit!", results[0].Title);
        Assert.Equal("https://atsu.moe/manga/94bKW", results[0].Url);
        Assert.Equal("What does it feel like to be strong?", results[0].Description);
    }

    [Fact]
    public async Task Search_prefers_the_medium_poster_and_puts_it_on_the_cdn()
    {
        var results = await WithSearch().SearchAsync("ippo");

        Assert.Equal(
            "https://cdn.atsu.moe/static/posters/SCOP96icfPTVVcHI-medium.avif",
            results[0].CoverUrl);
        Assert.Null(results[2].CoverUrl);
    }

    [Fact]
    public async Task Search_falls_back_to_the_english_title()
    {
        var results = await WithSearch().SearchAsync("ippo");

        Assert.Equal("Poster-less Title", results[2].Title);
    }

    [Fact]
    public async Task Search_asks_the_index_for_comics_only()
    {
        // The index holds prose novels too, and Typesense 400s without query_by, so both the
        // medium filter and the search fields have to be on the URL. Keying the fixture on them
        // means a request missing either answers 404 and fails this test.
        var source = SourceFor(new()
        {
            ["medium%3A%3DComic"] = FakeHttpClientFactory.Fixture("atsumaru-search.json")
        });

        var results = await source.SearchAsync("ippo");

        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task Search_results_carry_the_WeebCentral_id_the_index_records()
    {
        // Not a tracker - it is WeebCentral's own series id, so a confirmed Atsumaru match is also
        // a ready WeebCentral mapping. It costs nothing: the field rides the same search request.
        var results = await WithSearch().SearchAsync("ippo");

        Assert.Equal(
            "01J76XY7HF84B23C36Q8536BP7",
            results[0].ExternalIds![ExternalIdService.WeebCentral]);
        // The index doesn't have one for every title.
        Assert.Empty(results[1].ExternalIds!);
    }

    [Fact]
    public async Task GetExternalIds_reads_the_tracker_ids_the_series_page_carries()
    {
        var ids = await WithSeries().GetExternalIdsAsync("94bKW");

        Assert.NotNull(ids);
        // MangaBaka's own id among them, which is the key our metadata is stored under - so a
        // candidate can be confirmed without translating through another tracker.
        Assert.Equal("206", ids[ExternalIdService.MangaBaka]);
        Assert.Equal("7", ids[ExternalIdService.Mal]);
        // Sent as a bare number rather than a string on some rows.
        Assert.Equal("30007", ids[ExternalIdService.AniList]);
        Assert.Equal("23", ids[ExternalIdService.Kitsu]);
        Assert.Equal("9ft0dv5", ids[ExternalIdService.MangaUpdates]);
    }

    [Fact]
    public async Task GetSeries_reads_title_synopsis_status_and_poster()
    {
        var detail = await WithSeries().GetSeriesAsync("94bKW");

        Assert.Equal("Hajime no Ippo: Fighting Spirit!", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal("https://atsu.moe/manga/94bKW", detail.Url);
        Assert.Equal("What does it feel like to be strong?", detail.Description);
        // The series page's poster paths omit the /static prefix the search hits carry.
        Assert.Equal(
            "https://cdn.atsu.moe/static/posters/SCOP96icfPTVVcHI-medium.avif",
            detail.CoverUrl);
    }

    [Fact]
    public async Task ListChapters_keeps_one_scanlation_group_per_number()
    {
        var chapters = await WithSeries().ListChaptersAsync("94bKW");

        // scan-alpha covers four numbers to scan-beta's three, so alpha wins every number both
        // groups carry and beta only contributes the 2.5 alpha never scanlated.
        Assert.Equal(["alpha0", "alpha1", "alpha2", "beta2h", "alpha3"],
            chapters.Select(c => c.SourceChapterId));
    }

    [Fact]
    public async Task ListChapters_orders_ascending_and_keeps_fractional_numbers()
    {
        var chapters = await WithSeries().ListChaptersAsync("94bKW");

        Assert.Equal([0m, 1m, 2m, 2.5m, 3m], chapters.Select(c => c.Number));
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.All(chapters, c => Assert.Null(c.Volume));
        Assert.All(chapters, c => Assert.Null(c.ReleaseDate));
    }

    [Fact]
    public async Task ListChapters_drops_rows_without_a_number()
    {
        var chapters = await WithSeries().ListChaptersAsync("94bKW");

        // "Extras" carries no number, so there is nothing to identify it by.
        Assert.DoesNotContain(chapters, c => c.SourceChapterId == "nonumber");
    }

    [Fact]
    public async Task ListChapters_builds_reader_urls_and_drops_blank_titles()
    {
        var chapters = await WithSeries().ListChaptersAsync("94bKW");

        Assert.Equal("https://atsu.moe/read/94bKW/alpha1", chapters[1].Url);
        Assert.Equal("Round 1", chapters[1].Title);
        Assert.Null(chapters.Single(c => c.SourceChapterId == "alpha3").Title);
    }

    [Fact]
    public async Task GetPages_orders_by_page_number_and_resolves_cdn_urls()
    {
        var source = SourceFor(new()
        {
            ["read/chapter"] = FakeHttpClientFactory.Fixture("atsumaru-read.json")
        });

        var pages = await source.GetPagesAsync(new Maki.Core.Sources.SourceChapter(
            "atsumaru", "94bKW", "alpha1", "1", 1m, null, "Round 1", "en", null));

        Assert.Equal(
            [
                "https://cdn.atsu.moe/static/pages/94bKW/alpha1/0.webp",
                "https://cdn.atsu.moe/static/pages/94bKW/alpha1/1.webp",
                "https://cdn.atsu.moe/static/pages/94bKW/alpha1/2.webp"
            ],
            pages.Pages.Select(p => p.Url));
        Assert.All(pages.Pages, p => Assert.Equal("https://atsu.moe/", p.Headers!["Referer"]));
    }
}
