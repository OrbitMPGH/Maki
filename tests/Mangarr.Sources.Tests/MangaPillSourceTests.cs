using Mangarr.Sources.MangaPill;

namespace Mangarr.Sources.Tests;

public class MangaPillSourceTests
{
    private static MangaPillSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task Search_parses_results()
    {
        var source = SourceFor(new() { ["search"] = FakeHttpClientFactory.Fixture("mangapill-search.html") });

        var results = await source.SearchAsync("berserk");

        Assert.NotEmpty(results);
        var berserk = results[0];
        Assert.Equal("1/berserk", berserk.SourceSeriesId);
        Assert.Equal("Berserk", berserk.Title);
        Assert.Contains("/manga/1/berserk", berserk.Url);
        Assert.StartsWith("https://", berserk.CoverUrl);
    }

    [Fact]
    public async Task ListChapters_parses_and_orders_ascending()
    {
        var source = SourceFor(new() { ["manga/"] = FakeHttpClientFactory.Fixture("mangapill-series.html") });

        var chapters = await source.ListChaptersAsync("1/berserk");

        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.True(chapters.First().Number < chapters.Last().Number);
        Assert.Contains(chapters, c => c.Number == 385m);
    }

    [Fact]
    public async Task GetPages_returns_urls_with_referer()
    {
        var source = SourceFor(new() { ["chapters/"] = FakeHttpClientFactory.Fixture("mangapill-chapter.html") });

        var pages = await source.GetPagesAsync(new Mangarr.Core.Sources.SourceChapter(
            "mangapill", "1/berserk", "1-20385000/berserk-chapter-385", "385", 385, null, null, "en", null));

        Assert.NotEmpty(pages.Pages);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://", p.Url);
            Assert.Equal("https://mangapill.com/", p.Headers!["Referer"]);
        });
    }
}
