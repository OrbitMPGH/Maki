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
}
