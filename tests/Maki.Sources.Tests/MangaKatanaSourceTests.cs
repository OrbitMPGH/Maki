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
}
