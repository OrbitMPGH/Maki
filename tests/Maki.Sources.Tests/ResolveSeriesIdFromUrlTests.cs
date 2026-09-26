using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.Atsumaru;
using Maki.Sources.MangaDex;
using Maki.Sources.MangaFire;
using Maki.Sources.Mangakakalot;
using Maki.Sources.MangaPill;
using Maki.Sources.FlameComics;
using Maki.Sources.Olympus;
using Maki.Sources.WeebCentral;
using Maki.Sources.Webtoons;

namespace Maki.Sources.Tests;

public class ResolveSeriesIdFromUrlTests
{
    private static readonly FakeHttpClientFactory Factory = new([]);

    [Theory]
    [InlineData("https://mangadex.org/title/a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab", "a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab")]
    [InlineData("https://mangadex.org/title/a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab/some-slug", "a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab")]
    [InlineData("https://www.mangadex.org/title/a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab", "a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab")]
    [InlineData("https://mangadex.org/title/not-a-uuid", null)]
    [InlineData("https://mangadex.org/chapter/a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab", null)]
    [InlineData("https://example.com/title/a1b2c3d4-e5f6-4a1b-8c2d-0123456789ab", null)]
    public void MangaDex(string url, string? expected)
    {
        ISource source = new MangaDexSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://atsu.moe/manga/94bKW", "94bKW")]
    [InlineData("https://atsu.moe/manga/94bKW/", "94bKW")]
    [InlineData("https://atsu.moe/manga/94bKW/gallery", "94bKW")]
    // A reader link names the series first, so one copied mid-chapter still resolves.
    [InlineData("https://atsu.moe/read/94bKW/l4Sdzg4h", "94bKW")]
    [InlineData("https://atsu.moe/explore", null)]
    [InlineData("https://example.com/manga/94bKW", null)]
    public void Atsumaru(string url, string? expected)
    {
        ISource source = new AtsumaruSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://mangapill.com/manga/1/berserk", "1/berserk")]
    [InlineData("https://mangapill.com/manga/1/berserk/", "1/berserk")]
    [InlineData("https://mangapill.com/chapters/1-10001000/berserk-chapter-1", null)]
    [InlineData("https://mangafire.to/manga/1/berserk", null)]
    public void MangaPill(string url, string? expected)
    {
        ISource source = new MangaPillSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://weebcentral.com/series/01J76XYCT4JVR13RN6NT1480MD/Berserk", "01J76XYCT4JVR13RN6NT1480MD/Berserk")]
    [InlineData("https://weebcentral.com/chapters/01J76XYFKV2Q4NBZKJ0YD3TSJP", null)]
    public void WeebCentral(string url, string? expected)
    {
        ISource source = new WeebCentralSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://flamecomics.xyz/series/2", "2")]
    [InlineData("https://flamecomics.xyz/series/2/", "2")]
    // A chapter URL names the same series, so a link copied mid-read still resolves.
    [InlineData("https://flamecomics.xyz/series/2/364db6fd6bef182e", "2")]
    // /series/ ids are numeric; the novel catalogue lives elsewhere and must not resolve here.
    [InlineData("https://flamecomics.xyz/novels/8", null)]
    [InlineData("https://flamecomics.xyz/browse", null)]
    [InlineData("https://example.com/series/2", null)]
    public void FlameComics(string url, string? expected)
    {
        ISource source = new FlameComicsSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://www.webtoons.com/en/fantasy/tower-of-god/list?title_no=95", "fantasy/tower-of-god/95")]
    [InlineData("https://webtoons.com/en/fantasy/tower-of-god/list?title_no=95", "fantasy/tower-of-god/95")]
    [InlineData("https://www.webtoons.com/en/canvas/some-title/list?title_no=726081", "canvas/some-title/726081")]
    // A viewer URL names the same three parts, so a link copied mid-read still resolves.
    [InlineData("https://www.webtoons.com/en/fantasy/tower-of-god/season-1-ep-0/viewer?title_no=95&episode_no=1",
        "fantasy/tower-of-god/95")]
    [InlineData("https://www.webtoons.com/en/genres", null)]
    [InlineData("https://www.webtoons.com/es/fantasia/torre-de-dios/list?title_no=1461", null)]
    [InlineData("https://example.com/en/fantasy/tower-of-god/list?title_no=95", null)]
    public void Webtoons(string url, string? expected)
    {
        ISource source = new WebtoonsSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://mangafire.to/title/7wypj-konna-no-unmei-janai-kara-kanchigai-shinaidee", "7wypj-konna-no-unmei-janai-kara-kanchigai-shinaidee")]
    [InlineData("https://mangafire.to/title/7wypj-some-slug/extra", "7wypj-some-slug")]
    [InlineData("https://mangafire.to/home", null)]
    public void MangaFire(string url, string? expected)
    {
        // ResolveSeriesIdFromUrl is pure string parsing and never touches the browser.
        ISource source = new MangaFireSource(null!);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://www.mangakakalot.gg/manga/tower-of-god", "tower-of-god")]
    [InlineData("https://mangakakalot.gg/manga/tower-of-god/", "tower-of-god")]
    // A reader link names the series first, so one copied mid-chapter still resolves.
    [InlineData("https://www.mangakakalot.gg/manga/tower-of-god/chapter-652", "tower-of-god")]
    [InlineData("https://www.mangakakalot.gg/genre/action", null)]
    [InlineData("https://example.com/manga/tower-of-god", null)]
    public void Mangakakalot(string url, string? expected)
    {
        ISource source = new MangakakalotSource(null!);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    private const string OlympusSeriesUrl = "https://olympusxyz.com/series/comic-10-05-2025-nivel-solo5324";

    /// <summary>
    /// Olympus's URL carries only the slug, and the id-to-slug map only exists once the catalog has
    /// been fetched. The sync method never fetches, so it is null until something (here the async
    /// path) has loaded the catalog, and resolves from memory after that.
    /// </summary>
    [Fact]
    public async Task Olympus_sync_call_never_fetches_and_resolves_once_the_catalog_is_loaded()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json")
        });
        ISource source = new OlympusSource(fetcher);
        var seriesUrl = new Uri(OlympusSeriesUrl);

        Assert.Null(source.ResolveSeriesIdFromUrl(seriesUrl));
        Assert.Empty(fetcher.Requested);

        await source.ResolveSeriesIdFromUrlAsync(seriesUrl);

        Assert.Equal("10", source.ResolveSeriesIdFromUrl(seriesUrl));
        Assert.Null(source.ResolveSeriesIdFromUrl(
            new Uri("https://olympusxyz.com/capitulo/114575/comic-10-05-2025-nivel-solo5324")));
        Assert.Null(source.ResolveSeriesIdFromUrl(
            new Uri("https://example.com/series/comic-10-05-2025-nivel-solo5324")));
        Assert.Equal(1, fetcher.Requested.Count(u => u.Contains("api/series/list")));
    }

    [Fact]
    public async Task Olympus_async_call_resolves_on_a_cold_catalog()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json")
        });
        ISource source = new OlympusSource(fetcher);

        Assert.Equal("10", await source.ResolveSeriesIdFromUrlAsync(new Uri(OlympusSeriesUrl)));
        Assert.Equal(1, fetcher.Requested.Count(u => u.Contains("api/series/list")));

        // Not an Olympus series URL: rejected before any fetch.
        Assert.Null(await source.ResolveSeriesIdFromUrlAsync(
            new Uri("https://olympusxyz.com/capitulo/114575/comic-10-05-2025-nivel-solo5324")));
        Assert.Equal(1, fetcher.Requested.Count(u => u.Contains("api/series/list")));
    }

    /// <summary>
    /// A slug the cached catalog doesn't carry (a new series, or a slug that rotated after the
    /// catalog was fetched) forces a refetch, but no more than once a minute, so a URL that will
    /// never resolve can't make every attempt refetch the whole list.
    /// </summary>
    [Fact]
    public async Task Olympus_async_miss_refreshes_the_catalog_at_most_once_a_minute()
    {
        var fixtures = new Dictionary<string, string>
        {
            ["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json")
        };
        var fetcher = new FakeHtmlFetcher(fixtures);
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ISource source = new OlympusSource(fetcher, time: clock);
        var rotatedUrl = new Uri("https://olympusxyz.com/series/comic-11-05-2025-nivel-solo9999");

        Assert.Null(await source.ResolveSeriesIdFromUrlAsync(rotatedUrl));
        Assert.Null(await source.ResolveSeriesIdFromUrlAsync(rotatedUrl));
        Assert.Equal(1, fetcher.Requested.Count(u => u.Contains("api/series/list")));

        fixtures["api/series/list"] = FakeHttpClientFactory.Fixture("olympus-list.json")
            .Replace("10-05-2025-nivel-solo5324", "11-05-2025-nivel-solo9999");
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Null(await source.ResolveSeriesIdFromUrlAsync(rotatedUrl));
        Assert.Equal(1, fetcher.Requested.Count(u => u.Contains("api/series/list")));

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal("10", await source.ResolveSeriesIdFromUrlAsync(rotatedUrl));
        Assert.Equal(2, fetcher.Requested.Count(u => u.Contains("api/series/list")));
    }

    /// <summary>No fixture for the catalog list: the fetch fails, is logged, and resolves to null.</summary>
    [Fact]
    public async Task Olympus_resolves_null_when_the_catalog_fetch_fails()
    {
        ISource source = new OlympusSource(new FakeHtmlFetcher(new()));

        Assert.Null(await source.ResolveSeriesIdFromUrlAsync(new Uri(OlympusSeriesUrl)));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
