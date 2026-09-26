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
}
