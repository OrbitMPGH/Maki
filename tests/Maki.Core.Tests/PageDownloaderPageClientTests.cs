using System.Net;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

/// <summary>
/// A source on an admin-configured origin (<see cref="IPageClientSource"/>) swaps the client and the
/// address check and nothing else: cooldown, rate-limit mapping and the 404 that drives failover
/// still apply, and its own check is just as strict as the guard it replaces.
/// </summary>
public class PageDownloaderPageClientTests
{
    private const string LanPage = "http://192.168.1.20:4567/api/v1/manga/7/chapter/1/page/0";

    private sealed class LanSource : IPageClientSource
    {
        public string PageHttpClientName => "lan-pages";

        public bool IsPageUrlAllowed(Uri url) =>
            url.Scheme == Uri.UriSchemeHttp && url.Host == "192.168.1.20" && url.Port == 4567;
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Sends);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9])
            });
        }
    }

    private sealed class RecordingFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static ChapterPages Pages(params string[] urls) => new([.. urls.Select(u => new PageRequest(u))]);

    private static async Task InTempDir(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "maki-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            await body(dir);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static (PageDownloader Downloader, RecordingFactory Factory, StubHandler Handler, FakeCooldown Cooldown) Build(
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(status);
        var factory = new RecordingFactory(handler);
        var cooldown = new FakeCooldown();
        return (new PageDownloader(factory, cooldown, TimeProvider.System, NullLogger<PageDownloader>.Instance),
            factory, handler, cooldown);
    }

    [Fact]
    public async Task Fetches_a_lan_page_through_the_sources_own_client_and_still_waits_out_the_cooldown()
    {
        var (downloader, factory, handler, cooldown) = Build();

        await InTempDir(async dir =>
        {
            var files = await downloader.DownloadAsync(Pages(LanPage, LanPage + "1"), "suwayomi-1", dir, pageClient: new LanSource());

            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.True(File.Exists(f)));
        });

        Assert.Equal(["lan-pages"], factory.Names);
        Assert.Equal(2, handler.Sends);
        Assert.Equal(2, cooldown.Waits);
    }

    [Fact]
    public async Task Without_a_page_client_the_same_lan_page_is_refused()
    {
        var (downloader, factory, handler, _) = Build();

        await InTempDir(async dir =>
            await Assert.ThrowsAsync<BlockedDestinationException>(() => downloader.DownloadAsync(Pages(LanPage), "fake", dir)));

        Assert.Equal([PageDownloader.HttpClientName], factory.Names);
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("http://192.168.1.21:4567/api/v1/manga/7/chapter/1/page/0")]
    [InlineData("http://192.168.1.20:8080/api/v1/manga/7/chapter/1/page/0")]
    [InlineData("https://cdn.example.test/page/1.jpg")]
    public async Task A_url_outside_the_configured_origin_is_refused_before_anything_is_sent(string url)
    {
        var (downloader, _, handler, _) = Build();

        await InTempDir(async dir =>
            await Assert.ThrowsAsync<BlockedDestinationException>(
                () => downloader.DownloadAsync(Pages(url), "suwayomi-1", dir, pageClient: new LanSource())));

        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_rate_limit_still_becomes_a_RateLimitException(HttpStatusCode status)
    {
        var (downloader, _, _, _) = Build(status);

        await InTempDir(async dir =>
            await Assert.ThrowsAsync<RateLimitException>(
                () => downloader.DownloadAsync(Pages(LanPage), "suwayomi-1", dir, pageClient: new LanSource())));
    }

    [Fact]
    public async Task A_404_still_throws_the_HttpRequestException_that_drives_failover()
    {
        var (downloader, _, _, _) = Build(HttpStatusCode.NotFound);

        await InTempDir(async dir =>
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => downloader.DownloadAsync(Pages(LanPage), "suwayomi-1", dir, pageClient: new LanSource()));
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        });
    }
}
