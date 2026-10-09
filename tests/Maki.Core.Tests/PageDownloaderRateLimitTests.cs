using System.Net;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

public class PageDownloaderRateLimitTests
{
    /// <summary>A canned-response handler; every request gets the same message.</summary>
    private sealed class StubHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder());
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static PageDownloader DownloaderReturning(Func<HttpResponseMessage> responder) =>
        new(new StubFactory(new StubHandler(responder)), new FakeCooldown(), TimeProvider.System, NullLogger<PageDownloader>.Instance);

    private static ChapterPages OnePage() =>
        new([new PageRequest("https://example.test/page/1.jpg")]);

    [Fact]
    public async Task Throws_RateLimitException_On_429_With_RetryAfter_Delta()
    {
        var downloader = DownloaderReturning(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return r;
        });

        var dir = Path.Combine(Path.GetTempPath(), "maki-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => downloader.DownloadAsync(OnePage(), "fake", dir));
            Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task RetryAfter_Date_Counts_From_The_Injected_Clock_And_Never_Goes_Negative()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var dir = Path.Combine(Path.GetTempPath(), "maki-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (date, expected) in new[] { (now.AddSeconds(30), TimeSpan.FromSeconds(30)), (now.AddMinutes(-5), TimeSpan.Zero) })
            {
                var downloader = new PageDownloader(
                    new StubFactory(new StubHandler(() =>
                    {
                        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(date);
                        return r;
                    })),
                    new FakeCooldown(), new FixedClock(now), NullLogger<PageDownloader>.Instance);

                var ex = await Assert.ThrowsAsync<RateLimitException>(
                    () => downloader.DownloadAsync(OnePage(), "fake", dir));
                Assert.Equal(expected, ex.RetryAfter);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://cdn.test/a/001.png", ".png")]
    [InlineData("https://cdn.test/a/001.WEBP?x=1", ".WEBP")]
    [InlineData("https://cdn.test/image.php?id=7", ".jpg")]
    [InlineData("https://cdn.test/page.aspx", ".jpg")]
    [InlineData("https://cdn.test/a/001", ".jpg")]
    public void Page_files_keep_only_image_extensions(string url, string expected) =>
        Assert.Equal(expected, PageDownloader.ExtensionFor(url));

    [Fact]
    public async Task Throws_RateLimitException_On_503_Without_RetryAfter()
    {
        var downloader = DownloaderReturning(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var dir = Path.Combine(Path.GetTempPath(), "maki-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => downloader.DownloadAsync(OnePage(), "fake", dir));
            Assert.Null(ex.RetryAfter);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Ordinary_404_Throws_HttpRequestException_Not_RateLimit()
    {
        var downloader = DownloaderReturning(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        var dir = Path.Combine(Path.GetTempPath(), "maki-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(OnePage(), "fake", dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
