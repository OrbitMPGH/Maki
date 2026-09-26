using Maki.Core.Sources;
using Maki.Sources.GigaViewer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Sources.Tests;

public class GigaViewerSourceTests
{
    [Fact]
    public async Task SearchAsync_ParsesClassicMarkup()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["search?q="] = FakeHttpClientFactory.Fixture("gigaviewer-search-classic.html")
        });

        var results = await new ShonenJumpPlusSource(factory).SearchAsync("SPY");

        // The second result in the fixture ("SPY×FAMILY カラー版") links only /volume/, no
        // /episode/ entry point, and must be skipped.
        var hit = Assert.Single(results);
        Assert.Equal("10834108156648240735", hit.SourceSeriesId);
        Assert.Equal("SPY×FAMILY", hit.Title);
        Assert.Equal("https://shonenjumpplus.com/episode/10834108156648240735", hit.Url);
        Assert.NotNull(hit.CoverUrl);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyForABlankQueryWithoutRequesting()
    {
        var factory = new FakeHttpClientFactory(new());

        var results = await new ShonenJumpPlusSource(factory).SearchAsync("   ");

        Assert.Empty(results);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task SearchAsync_ParsesNextJsMarkup()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["search?q="] = FakeHttpClientFactory.Fixture("gigaviewer-search-next.html")
        });

        var results = await new MagcomiSource(factory).SearchAsync("ノヴァ");

        var hit = Assert.Single(results);
        Assert.Equal("2550912964888518822", hit.SourceSeriesId);
        Assert.Equal("魔物ノ森ノ少女ノヴァ", hit.Title);
    }

    [Fact]
    public async Task SearchAsync_StripsQueryAndFragmentAndRejectsNonDigitIds()
    {
        const string html =
            "<ul class='series-list'>" +
            "<li data-title='Clean'><a href='/episode/123?ref=search'>1</a></li>" +
            "<li data-title='Fragment'><a href='/episode/456#top'>1</a></li>" +
            "<li data-title='Bad'><a href='/episode/not-a-number'>1</a></li>" +
            "</ul>";

        var factory = new FakeHttpClientFactory(new() { ["search?q="] = html });

        var results = await new ShonenJumpPlusSource(factory).SearchAsync("x");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.SourceSeriesId == "123");
        Assert.Contains(results, r => r.SourceSeriesId == "456");
        Assert.DoesNotContain(results, r => r.Title == "Bad");
    }

    [Fact]
    public async Task GetSeriesAsync_ParsesDetailFromEpisodePage()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["episode/10834108156648240735"] = FakeHttpClientFactory.Fixture("gigaviewer-episode.html")
        });

        var detail = await new ShonenJumpPlusSource(factory).GetSeriesAsync("10834108156648240735");

        Assert.Equal("SPY×FAMILY", detail.Title);
        Assert.Equal("https://shonenjumpplus.com/episode/10834108156648240735", detail.Url);
        Assert.Contains("cdn-ak-img.shonenjumpplus.com", detail.CoverUrl);
        Assert.False(string.IsNullOrWhiteSpace(detail.Description));
    }

    [Fact]
    public async Task ListChaptersAsync_FiltersFreeAndParsesNumbers()
    {
        // The two recorded pagination pages (offset 0 and 150) together carry 81 of
        // SPY×FAMILY's 181 episodes; only 14 are free, and 10 of those are unnumbered
        // specials that Normalize keeps apart by title.
        var factory = new FakeHttpClientFactory(new()
        {
            ["episode/10834108156648240735"] = FakeHttpClientFactory.Fixture("gigaviewer-episode.html"),
            ["offset=0"] = FakeHttpClientFactory.Fixture("gigaviewer-episodes-0.json"),
            ["offset=50"] = FakeHttpClientFactory.Fixture("gigaviewer-episodes-150.json")
        });

        var chapters = await new ShonenJumpPlusSource(factory).ListChaptersAsync("10834108156648240735");

        Assert.Equal(14, chapters.Count);
        Assert.Contains(chapters, c => c.Number == 1m);
        Assert.Contains(chapters, c => c.Number == 2m);
        Assert.Contains(chapters, c => c.Number == 140m);
        Assert.Contains(chapters, c => c.Number == 139.3m);
        var specials = chapters.Where(c => c.Number is null).ToList();
        Assert.Equal(10, specials.Count);
        Assert.Equal(10, specials.Select(c => c.Title).Distinct().Count());
        Assert.All(chapters, c => Assert.Equal("ja", c.Language));
        Assert.All(chapters, c => Assert.Equal("shonenjumpplus", c.SourceName));
        // Ascending by number, nulls first.
        Assert.Null(chapters[0].Number);
        Assert.Equal(1m, chapters[10].Number);
    }

    [Fact]
    public async Task ListChaptersAsync_TreatsPurchaseInfoFreeAsFreeWhenStatusIsNull()
    {
        // Sunday Webry and Tonari publish status: null on every item and rely on
        // purchase_info.is_free alone.
        var factory = new FakeHttpClientFactory(new()
        {
            ["episode/12207421984255619382"] = FakeHttpClientFactory.Fixture("gigaviewer-episode.html"),
            ["offset=0"] = FakeHttpClientFactory.Fixture("gigaviewer-episodes-statusnull.json"),
            ["offset=1"] = "[]"
        });

        var chapters = await new SundayWebrySource(factory).ListChaptersAsync("12207421984255619382");

        var chapter = Assert.Single(chapters);
        Assert.Equal("12207421984255619382", chapter.SourceChapterId);
        Assert.Equal("ja", chapter.Language);
    }

    [Fact]
    public async Task GetPagesAsync_ThrowsChapterLockedExceptionForALockedEpisode()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["episode/9253191255607512279"] = FakeHttpClientFactory.Fixture("gigaviewer-episode-locked.html")
        });

        var chapter = new SourceChapter(
            "shonenjumpplus", "10834108156648240735", "9253191255607512279",
            "141話", 141m, null, null, "ja", null,
            "https://shonenjumpplus.com/episode/9253191255607512279");

        await Assert.ThrowsAsync<ChapterLockedException>(
            () => new ShonenJumpPlusSource(factory).GetPagesAsync(chapter));
    }

    [Fact]
    public async Task GetPagesAsync_DescramblesEveryPageThroughTheDataHatch()
    {
        var factory = new FakeHttpClientFactory(
            new()
            {
                ["episode/10834108156648240735"] = FakeHttpClientFactory.Fixture("gigaviewer-episode.html")
            },
            new()
            {
                // Every page image on this fixture shares this CDN path prefix; the same tiny
                // synthetic image stands in for all 71 of them, since the point of this test is
                // the plumbing (Data set, Referer sent, valid JPEG out) rather than each page's
                // real pixels.
                ["cdn-ak-img.shonenjumpplus.com/public/page/2/"] = BuildSyntheticPagePng()
            });

        var chapter = new SourceChapter(
            "shonenjumpplus", "10834108156648240735", "10834108156648240735",
            "1話", 1m, null, null, "ja", null,
            "https://shonenjumpplus.com/episode/10834108156648240735");

        var pages = await new ShonenJumpPlusSource(factory).GetPagesAsync(chapter);

        Assert.Equal(71, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
        {
            Assert.NotNull(p.Data);
            Assert.NotEmpty(p.Data!);
            Assert.Equal(chapter.Url, p.Headers?["Referer"]);
        });

        using var decoded = Image.Load(pages.Pages[0].Data!);
        Assert.Equal("JPEG", decoded.Metadata.DecodedImageFormat?.Name);
    }

    [Fact]
    public async Task GetPagesAsync_ReturnsPlainPageRequestsWhenChoJuGigaIsNotBaku()
    {
        // No GigaViewer site sampled serves anything but "baku", but the else branch (plain
        // PageRequest, no Data) still needs coverage.
        const string html =
            "<html><body><script id='episode-json' type='text/json' data-value='" +
            "{&quot;readableProduct&quot;:{&quot;pageStructure&quot;:{" +
            "&quot;choJuGiga&quot;:&quot;plain&quot;,&quot;pages&quot;:[" +
            "{&quot;type&quot;:&quot;main&quot;,&quot;src&quot;:&quot;https://cdn.example.test/page1.jpg&quot;}]}}}" +
            "'></script></body></html>";

        var factory = new FakeHttpClientFactory(new() { ["episode/1"] = html });

        var chapter = new SourceChapter(
            "shonenjumpplus", "1", "1", "1話", 1m, null, null, "ja", null,
            "https://shonenjumpplus.com/episode/1");

        var pages = await new ShonenJumpPlusSource(factory).GetPagesAsync(chapter);

        var page = Assert.Single(pages.Pages);
        Assert.Equal("https://cdn.example.test/page1.jpg", page.Url);
        Assert.Null(page.Data);
        Assert.Equal("https://shonenjumpplus.com/episode/1", page.Headers?["Referer"]);
    }

    [Fact]
    public async Task GetPagesAsync_ThrowsInvalidOperationExceptionWhenEpisodeJsonIsMissing()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["episode/1"] = "<html><body>not a gigaviewer page any more</body></html>"
        });

        var chapter = new SourceChapter(
            "shonenjumpplus", "1", "1", "1話", 1m, null, null, "ja", null,
            "https://shonenjumpplus.com/episode/1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ShonenJumpPlusSource(factory).GetPagesAsync(chapter));
    }

    [Fact]
    public async Task ListChaptersAsync_ReadsAggregateIdFromThePaginationDivWhenJsValveIsAbsent()
    {
        // Comic Zenon's episode page carries no script.js-valve, only the pagination div's
        // data-aggregate-id (confirmed live); this exercises that fallback branch specifically.
        var factory = new FakeHttpClientFactory(new()
        {
            ["episode/2550912965114131371"] = FakeHttpClientFactory.Fixture("gigaviewer-zenon-episode.html"),
            ["offset=0"] = "[]"
        });

        var chapters = await new ComicZenonSource(factory).ListChaptersAsync("2550912965114131371");

        Assert.Empty(chapters);
        Assert.Contains(factory.Requests, url => url.Contains("aggregate_id=2550912965114108244"));
    }

    private static byte[] BuildSyntheticPagePng()
    {
        using var image = new Image<Rgba32>(64, 64);
        var color = new Rgba32(200, 100, 50, 255);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                image[x, y] = color;
            }
        }

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
