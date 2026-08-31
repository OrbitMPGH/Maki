using Maki.Core.Sources;
using Maki.Sources.WeebCentral;

namespace Maki.Sources.Tests;

public class WeebCentralSourceTests
{
    private static WeebCentralSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task Search_parses_results()
    {
        var source = SourceFor(new() { ["search/data"] = FakeHttpClientFactory.Fixture("weebcentral-search.html") });

        var results = await source.SearchAsync("berserk");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Title == "Berserk" && r.SourceSeriesId.StartsWith("01J76XY7EF75DJNQCV04HTPDZK"));
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
