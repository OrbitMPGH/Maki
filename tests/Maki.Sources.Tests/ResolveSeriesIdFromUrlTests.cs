using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.Atsumaru;
using Maki.Sources.MangaDex;
using Maki.Sources.MangaFire;
using Maki.Sources.MangaPill;
using Maki.Sources.FlameComics;
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
}
