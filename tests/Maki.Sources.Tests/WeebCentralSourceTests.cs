using Maki.Core.Sources;
using Maki.Sources.WeebCentral;

namespace Maki.Sources.Tests;

public class WeebCentralSourceTests
{
    /// <summary>Cloudflare's managed-challenge shell, as the direct path sees it with no clearance.</summary>
    private const string ChallengePage = """
        <!DOCTYPE html><html><head><title>Just a moment...</title></head>
        <body><form id="challenge-form" action="/series/x?__cf_chl_f_tk=abc" method="POST"></form></body></html>
        """;

    private static WeebCentralSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHtmlFetcher(responses));

    [Fact]
    public async Task Search_parses_results()
    {
        var source = SourceFor(new() { ["search/data"] = FakeHttpClientFactory.Fixture("weebcentral-search.html") });

        var results = await source.SearchAsync("berserk");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Title == "Berserk" && r.SourceSeriesId.StartsWith("01J76XY7EF75DJNQCV04HTPDZK"));
    }

    [Fact]
    public async Task Search_asks_the_site_for_the_absolute_search_url()
    {
        var fetcher = new FakeHtmlFetcher(new() { ["search/data"] = FakeHttpClientFactory.Fixture("weebcentral-search.html") });

        await new WeebCentralSource(fetcher).SearchAsync("berserk");

        var url = Assert.Single(fetcher.Requested);
        Assert.StartsWith("https://weebcentral.com/search/data?", url);
        Assert.Contains("text=berserk", url);
    }

    [Fact]
    public async Task Search_throws_on_a_challenge_page_rather_than_reading_no_results()
    {
        // The fetcher hands a recognised challenge to FlareSolverr itself; one that reaches the
        // parser anyway must not turn into an empty hit list that auto-match takes as "not here".
        var source = SourceFor(new() { ["search/data"] = ChallengePage });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.SearchAsync("berserk"));
    }

    [Fact]
    public async Task GetExternalIds_reads_the_tracker_row_on_the_series_page()
    {
        var source = SourceFor(new() { ["series/"] = FakeHttpClientFactory.Fixture("weebcentral-series.html") });

        var ids = await source.GetExternalIdsAsync("01J76XY7E9FNDZ1DBBM6PBJPFK");

        Assert.NotNull(ids);
        Assert.Equal("30013", ids[ExternalIdService.AniList]);
        Assert.Equal("pb8uwds", ids[ExternalIdService.MangaUpdates]);
        // The site links no MyAnimeList entry, and the official-source link is not a tracker.
        Assert.DoesNotContain(ExternalIdService.Mal, ids.Keys);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task ListChapters_parses_numbers_and_dates()
    {
        var source = SourceFor(new() { ["full-chapter-list"] = FakeHttpClientFactory.Fixture("weebcentral-chapters.html") });

        var chapters = await source.ListChaptersAsync("01J76XY7EF75DJNQCV04HTPDZK/Berserk");

        Assert.NotEmpty(chapters);
        var ch385 = chapters.FirstOrDefault(c => c.Number == 385m);
        Assert.NotNull(ch385);
        Assert.Equal("01KVZQCN6YH5S98R99YMEA5X29", ch385.SourceChapterId);
        Assert.NotNull(ch385.ReleaseDate);
    }

    [Fact]
    public async Task ListChapters_throws_on_a_challenge_page_rather_than_reading_no_chapters()
    {
        // An empty list here would read as "the series has no chapters" and quietly stall a
        // monitored series instead of failing the sync.
        var source = SourceFor(new() { ["full-chapter-list"] = ChallengePage });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ListChaptersAsync("01J76XY7EF75DJNQCV04HTPDZK/Berserk"));
    }

    [Fact]
    public async Task ListChapters_keeps_distinct_unnumbered_specials()
    {
        var source = SourceFor(new()
        {
            ["full-chapter-list"] = FakeHttpClientFactory.Fixture("weebcentral-chapters-unnumbered.html")
        });

        var chapters = await source.ListChaptersAsync("01J76XY7EF75DJNQCV04HTPDZK/Berserk");

        Assert.Equal(2, chapters.Count);
        Assert.Contains(chapters, c => c.Title == "Oneshot");
        Assert.Contains(chapters, c => c.Title == "Extra");
        Assert.All(chapters, c => Assert.Null(c.Number));
    }

    [Theory]
    [InlineData("Chapter 12", 12)]
    [InlineData("Episode 124", 124)]
    [InlineData("Plot 49", 49)]
    [InlineData("Mischief 225", 225)]
    [InlineData("Mischief 10.5", 10.5)]
    public void ParseLabel_takes_the_number_whatever_the_series_calls_its_chapters(string label, double expected)
    {
        Assert.Equal((decimal)expected, WeebCentralSource.ParseLabel(label).Number);
    }

    [Theory]
    [InlineData("Oneshot")]
    [InlineData("Extra")]
    public void ParseLabel_leaves_unnumbered_labels_unnumbered(string label)
    {
        Assert.Null(WeebCentralSource.ParseLabel(label).Number);
    }

    [Fact]
    public async Task GetPages_returns_urls_with_referer()
    {
        var source = SourceFor(new() { ["/images"] = FakeHttpClientFactory.Fixture("weebcentral-images.html") });

        var pages = await source.GetPagesAsync(new Maki.Core.Sources.SourceChapter(
            "weebcentral", "x", "01KVZQCN6YH5S98R99YMEA5X29", "385", 385, null, null, "en", null));

        Assert.NotEmpty(pages.Pages);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://", p.Url);
            Assert.Equal("https://weebcentral.com/", p.Headers!["Referer"]);
        });
        Assert.DoesNotContain(pages.Pages, p => p.Url.Contains("broken_image"));
    }
}
