using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.Atsumaru;
using Maki.Sources.MangaDex;
using Maki.Sources.MangaFire;
using Maki.Sources.Mangakakalot;
using Maki.Sources.MangaPill;
using Maki.Sources.FlameComics;
using Maki.Sources.GigaViewer;
using Maki.Sources.WeebCentral;
using Maki.Sources.Webtoons;
using Maki.Sources.Toonily;
using Maki.Sources.MangaLib;
using Maki.Sources.Dynasty;
using Maki.Sources.AnimeSama;
using Maki.Sources.ManhwaWeb;
using Maki.Sources.Olympus;
using Maki.Sources.Shinigami;
using Maki.Sources.Manhwa18Net;
using Maki.Sources.CuuTruyen;
using Maki.Sources.MangaWorld;
using Maki.Sources.MangaTube;

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
    // Non-English locales carry their own title_no and get a 4-segment id, not a match failure.
    [InlineData("https://www.webtoons.com/es/fantasia/torre-de-dios/list?title_no=1461", "es/fantasia/torre-de-dios/1461")]
    [InlineData("https://www.webtoons.com/es/fantasy/tower-of-god/list?title_no=1718", "es/fantasy/tower-of-god/1718")]
    // A locale's viewer URL names the same four parts, so a link copied mid-read still resolves.
    [InlineData("https://www.webtoons.com/es/fantasy/tower-of-god/t-1-ep-000/viewer?title_no=1718&episode_no=1",
        "es/fantasy/tower-of-god/1718")]
    [InlineData("https://www.webtoons.com/zh-hant/fantasy/tower-of-god/list?title_no=160", "zh-hant/fantasy/tower-of-god/160")]
    [InlineData("https://www.webtoons.com/th/canvas/sky-tower-moon-tower-the-aureum-path/list?title_no=155834",
        "th/canvas/sky-tower-moon-tower-the-aureum-path/155834")]
    // ja/ko are not served locales of this site (unlike the seven in LocaleLanguages).
    [InlineData("https://www.webtoons.com/ja/fantasy/tower-of-god/list?title_no=95", null)]
    [InlineData("https://www.webtoons.com/ko/fantasy/tower-of-god/list?title_no=95", null)]
    [InlineData("https://example.com/en/fantasy/tower-of-god/list?title_no=95", null)]
    [InlineData("https://example.com/es/fantasy/tower-of-god/list?title_no=1718", null)]
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

    [Theory]
    [InlineData("https://shonenjumpplus.com/episode/10834108156648240735", "10834108156648240735")]
    [InlineData("https://shonenjumpplus.com/episode/10834108156648240735/", "10834108156648240735")]
    [InlineData("https://shonenjumpplus.com/volume/4856001361007452473", null)]
    [InlineData("https://shonenjumpplus.com/series", null)]
    [InlineData("https://shonenjumpplus.com/search?q=x", null)]
    // Digits only: a slug must not resolve here.
    [InlineData("https://shonenjumpplus.com/episode/not-a-number", null)]
    // Every GigaViewer site is its own source and only accepts its own host.
    [InlineData("https://www.sunday-webry.com/episode/3269754496548997914", null)]
    [InlineData("https://comic-days.com/episode/10834108156648240735", null)]
    public void GigaViewer(string url, string? expected)
    {
        ISource source = new ShonenJumpPlusSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://toonily.com/serie/secret-class-38c3e37a/", "secret-class-38c3e37a")]
    [InlineData("https://toonily.com/serie/secret-class-38c3e37a", "secret-class-38c3e37a")]
    // Legacy URL shape; still 301s on the live site but must resolve from a stored link either way.
    [InlineData("https://toonily.com/webtoon/secret-class-38c3e37a/", "secret-class-38c3e37a")]
    // More than one segment after the marker names a chapter, not the series.
    [InlineData("https://toonily.com/serie/secret-class-38c3e37a/chapter-242/", null)]
    [InlineData("https://toonily.com/search/secret-class", null)]
    [InlineData("https://example.com/serie/secret-class-38c3e37a/", null)]
    public void Toonily(string url, string? expected)
    {
        ISource source = new ToonilySource(null!);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://mangalib.me/ru/manga/206--one-piece", "206--one-piece")]
    [InlineData("https://mangalib.me/ru/manga/206--one-piece/", "206--one-piece")]
    [InlineData("https://www.mangalib.me/ru/manga/206--one-piece", "206--one-piece")]
    // A slug with no leading "{id}--" isn't a real series id on this site.
    [InlineData("https://mangalib.me/ru/manga/one-piece", null)]
    // Chapter reader link: names the series but not through "/manga/", so it doesn't resolve here.
    [InlineData("https://mangalib.me/ru/206--one-piece/read/v108/c1194", null)]
    // Legacy host-root link, no "/manga/" segment.
    [InlineData("https://mangalib.me/206--one-piece", null)]
    [InlineData("https://hentailib.me/ru/manga/206--one-piece", null)]
    public void MangaLib(string url, string? expected)
    {
        ISource source = new MangaLibSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://dynasty-scans.com/series/citrus", "citrus")]
    [InlineData("https://dynasty-scans.com/series/citrus.json", "citrus")]
    [InlineData("https://dynasty-scans.com/series/citrus_1", "citrus_1")]
    [InlineData("https://dynasty-scans.com/chapters/citrus_ch01", null)]
    [InlineData("https://dynasty-scans.com/doujins/some-doujin", null)]
    [InlineData("https://dynasty-scans.com/anthologies/some-anthology", null)]
    [InlineData("https://dynasty-scans.com/authors/saburouta", null)]
    [InlineData("https://example.com/series/citrus", null)]
    public void DynastyScans(string url, string? expected)
    {
        ISource source = new DynastySource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://anime-sama.to/catalogue/one-piece/", "one-piece")]
    [InlineData("https://anime-sama.to/catalogue/one-piece", "one-piece")]
    [InlineData("https://anime-sama.to/catalogue/one-piece/scan_noir-et-blanc/vf/", "one-piece/scan_noir-et-blanc/vf")]
    [InlineData("https://www.anime-sama.to/catalogue/one-piece/scan/vf/", "one-piece/scan/vf")]
    // Anime paths (not "scan...") on the same three-segment shape must not resolve.
    [InlineData("https://anime-sama.to/catalogue/one-piece/saison1/vostfr/", null)]
    [InlineData("https://anime-sama.to/catalogue/", null)]
    [InlineData("https://anime-sama.to/s2/scans/One%20Piece/1194/1.jpg", null)]
    [InlineData("https://example.com/catalogue/one-piece/", null)]
    public void AnimeSama(string url, string? expected)
    {
        ISource source = new AnimeSamaSource(null!);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://manhwaweb.com/manhwa/solo-leveling-ragnarok_1783909089054", "solo-leveling-ragnarok_1783909089054")]
    [InlineData("https://www.manhwaweb.com/manhwa/solo-leveling-ragnarok_1783909089054", "solo-leveling-ragnarok_1783909089054")]
    [InlineData("https://manhwaweb.com/manhwa/solo-leveling-ragnarok_1783909089054/", "solo-leveling-ragnarok_1783909089054")]
    [InlineData("https://manhwaweb.com/leer/solo-leveling-ragnarok_1783909089054-1_01", null)]
    [InlineData("https://manhwawebbackend-production.up.railway.app/manhwa/solo-leveling-ragnarok_1783909089054", null)]
    [InlineData("https://example.com/manhwa/solo-leveling-ragnarok_1783909089054", null)]
    public void ManhwaWeb(string url, string? expected)
    {
        ISource source = new ManhwaWebSource(new FakeHttpClientFactory(new()));
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

    [Theory]
    [InlineData("https://11.shinigami.asia/series/5c612573-fe38-42df-8618-dc3de1c9d04a", "5c612573-fe38-42df-8618-dc3de1c9d04a")]
    [InlineData("https://12.shinigami.asia/series/5c612573-fe38-42df-8618-dc3de1c9d04a/", "5c612573-fe38-42df-8618-dc3de1c9d04a")]
    [InlineData("https://shinigami.asia/series/5c612573-fe38-42df-8618-dc3de1c9d04a", "5c612573-fe38-42df-8618-dc3de1c9d04a")]
    [InlineData("https://11.shinigami.asia/chapter/5c612573-fe38-42df-8618-dc3de1c9d04a", null)]
    [InlineData("https://api.shngm.io/v1/manga/detail/5c612573-fe38-42df-8618-dc3de1c9d04a", null)]
    [InlineData("https://shinigami.example/series/5c612573-fe38-42df-8618-dc3de1c9d04a", null)]
    [InlineData("https://11.shinigami.asia/series/not-a-guid", null)]
    public void Shinigami(string url, string? expected)
    {
        ISource source = new ShinigamiSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://manhwa18.net/manga/secret-class", "secret-class")]
    [InlineData("https://manhwa18.net/manga/secret-class/", "secret-class")]
    // A chapter URL adds a second segment and must not resolve to its series.
    [InlineData("https://manhwa18.net/manga/secret-class/chapter-318", null)]
    [InlineData("https://manhwa18.net/tim-kiem?q=secret+class", null)]
    // manhwa18.com is an unrelated site; must not be confused with manhwa18.net.
    [InlineData("https://manhwa18.com/manga/secret-class", null)]
    [InlineData("https://example.com/manga/secret-class", null)]
    public void Manhwa18Net(string url, string? expected)
    {
        ISource source = new Manhwa18NetSource(null!);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://cuutruyen.net/mangas/2637", "2637")]
    [InlineData("https://cuutruyen.net/mangas/2637/", "2637")]
    [InlineData("https://cuutruyen.net/mangas/2637/chapters/87003", null)]
    [InlineData("https://cuutruyen.net/mangas/not-a-number", null)]
    [InlineData("https://cuutruyen.net/", null)]
    [InlineData("https://example.com/mangas/2637", null)]
    public void CuuTruyen(string url, string? expected)
    {
        ISource source = new CuuTruyenSource(null!, Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://www.mangaworld.mx/manga/1708/one-piece", "1708/one-piece")]
    [InlineData("https://www.mangaworld.mx/manga/1708/one-piece/", "1708/one-piece")]
    [InlineData("https://mangaworld.mx/manga/1708/one-piece", "1708/one-piece")]
    // The numeric id alone would need GetSeriesAsync to follow a redirect to learn the slug; not done.
    [InlineData("https://www.mangaworld.mx/manga/1708", null)]
    [InlineData("https://www.mangaworld.mx/manga/1708/one-piece/read/6ab6b1f9c1f8362c329080c7", null)]
    [InlineData("https://www.mangaworld.mx/archive?keyword=one+piece", null)]
    [InlineData("https://example.com/manga/1708/one-piece", null)]
    public void MangaWorld(string url, string? expected)
    {
        ISource source = new MangaWorldSource(null!);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://comic.naver.com/webtoon/list?titleId=769209", "769209")]
    [InlineData("https://m.comic.naver.com/webtoon/list?titleId=769209", "769209")]
    [InlineData("https://comic.naver.com/webtoon/list?titleId=769209&tab=wed", "769209")]
    [InlineData("https://comic.naver.com/webtoon/detail?titleId=769209&no=182", null)]
    [InlineData("https://comic.naver.com/bestChallenge/list?titleId=769209", null)]
    [InlineData("https://comic.naver.com/challenge/list?titleId=769209", null)]
    [InlineData("https://www.webtoons.com/en/fantasy/tower-of-god/list?title_no=95", null)]
    public void NaverWebtoon(string url, string? expected)
    {
        ISource source = new Maki.Sources.NaverWebtoon.NaverWebtoonSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://www.manhuagui.com/comic/1128/", "1128")]
    [InlineData("https://www.manhuagui.com/comic/1128", "1128")]
    [InlineData("https://tw.manhuagui.com/comic/1128", "1128")]
    [InlineData("https://mhgui.com/comic/1128/", "1128")]
    // A chapter URL is not a series page.
    [InlineData("https://www.manhuagui.com/comic/1128/909042.html", null)]
    [InlineData("https://www.manhuagui.com/s/%E6%B5%B7%E8%B4%BC%E7%8E%8B_p1.html", null)]
    [InlineData("https://example.com/comic/1128/", null)]
    public void Manhuagui(string url, string? expected)
    {
        ISource source = new Manhuagui.ManhuaguiSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("https://manga-tube.me/series/one_piece", "one_piece")]
    [InlineData("https://manga-tube.me/series/one_piece/", "one_piece")]
    // A chapter-reader link is rejected rather than resolved to its series, unlike most sources.
    [InlineData("https://manga-tube.me/series/one_piece/read/19830/1", null)]
    [InlineData("https://manga-tube.me/api/manga/one_piece", null)]
    [InlineData("https://example.com/series/one_piece", null)]
    public void MangaTube(string url, string? expected)
    {
        ISource source = new MangaTubeSource(Factory);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }
}
