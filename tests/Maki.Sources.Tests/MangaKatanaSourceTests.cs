using Maki.Core.Sources;
using Maki.Sources.MangaKatana;

namespace Maki.Sources.Tests;

public class MangaKatanaSourceTests
{
    [Fact]
    public async Task Search_returns_empty_when_site_404s()
    {
        // MangaKatana answers 404 for a search that matches nothing, which used to
        // surface as an exception through auto-match and /api/v1/search/source.
        var source = new MangaKatanaSource(new FakeHttpClientFactory([]));

        var results = await source.SearchAsync("a title that matches nothing");

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("https://mangakatana.com/manga/slug.123", "slug.123")]
    [InlineData("https://mangakatana.com/manga/slug.123/", "slug.123")]
    [InlineData("https://mangakatana.com/manga/slug.123/c1050", null)]
    [InlineData("https://example.com/manga/slug.123", null)]
    public void ResolveSeriesIdFromUrl_accepts_only_bare_series_pages(string url, string? expected)
    {
        ISource source = new MangaKatanaSource(new FakeHttpClientFactory([]));

        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    private static SourceChapter ChapterOf(string id) =>
        new("mangakatana", "slug.123", id, "1", 1m, null, null, "en", null);

    [Fact]
    public async Task GetPages_reads_the_array_the_loader_script_names()
    {
        const string html = """
            <script>var other=['x'];var thzq=['https://i.test/1.jpg','https://i.test/2.jpg'];
            $(function(){ loadImg('data-src', thzq); });</script>
            """;
        var source = new MangaKatanaSource(new FakeHttpClientFactory(new() { ["manga/slug.123/c1"] = html }));

        var pages = await source.GetPagesAsync(ChapterOf("c1"));

        Assert.Equal(["https://i.test/1.jpg", "https://i.test/2.jpg"], pages.Pages.Select(p => p.Url));
    }

    [Fact]
    public async Task GetPages_throws_when_the_markup_has_no_image_script()
    {
        var source = new MangaKatanaSource(
            new FakeHttpClientFactory(new() { ["manga/slug.123/c1"] = "<html><body>changed</body></html>" }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPagesAsync(ChapterOf("c1")));

        Assert.Contains("manga/slug.123/c1", ex.Message);
    }
}
