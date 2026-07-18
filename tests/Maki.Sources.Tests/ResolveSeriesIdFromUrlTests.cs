using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.MangaDex;
using Maki.Sources.MangaFire;
using Maki.Sources.MangaPill;
using Maki.Sources.WeebCentral;

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
    [InlineData("https://mangafire.to/title/7wypj-konna-no-unmei-janai-kara-kanchigai-shinaidee", "7wypj-konna-no-unmei-janai-kara-kanchigai-shinaidee")]
    [InlineData("https://mangafire.to/title/7wypj-some-slug/extra", "7wypj-some-slug")]
    [InlineData("https://mangafire.to/home", null)]
    public void MangaFire(string url, string? expected)
    {
        ISource source = new MangaFireSource(new ChallengeAwareFetcher(null!, null!, null!, null!));
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }
}
