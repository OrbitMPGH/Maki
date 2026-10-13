using Maki.Sources.TCBScans;

namespace Maki.Sources.Tests;

public class TCBScansSourceTests
{
    private static TCBScansSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task ListChapters_parses_numbers_from_the_series_page()
    {
        const string html =
            """
            <html><body>
            <a href="/chapters/7991/one-piece-chapter-1187">Chapter 1187</a>
            <a href="/chapters/7991/one-piece-chapter-1186">Chapter 1186</a>
            </body></html>
            """;

        var chapters = await SourceFor(new() { ["mangas/"] = html }).ListChaptersAsync("5/one-piece");

        Assert.Equal(2, chapters.Count);
        Assert.Equal(1186m, chapters[0].Number);
        Assert.Equal(1187m, chapters[1].Number);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
    }

    [Fact]
    public async Task ListChapters_keeps_distinct_unnumbered_specials()
    {
        const string html =
            """
            <html><body>
            <a href="/chapters/7991/one-piece-oneshot">Oneshot</a>
            <a href="/chapters/7991/one-piece-extra">Extra</a>
            </body></html>
            """;

        var chapters = await SourceFor(new() { ["mangas/"] = html }).ListChaptersAsync("5/one-piece");

        Assert.Equal(2, chapters.Count);
        Assert.Contains(chapters, c => c.Title == "Oneshot");
        Assert.Contains(chapters, c => c.Title == "Extra");
        Assert.All(chapters, c => Assert.Null(c.Number));
    }

    [Fact]
    public async Task Search_throws_when_the_catalog_page_lists_no_series()
    {
        var source = SourceFor(new() { ["projects"] = "<html><body>Just a moment...</body></html>" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.SearchAsync("one piece"));
    }
}
