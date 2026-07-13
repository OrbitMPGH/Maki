using System.Net;
using Mangarr.Core.Download;
using Mangarr.Core.Http;
using Mangarr.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mangarr.Core.Tests;

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
        new(new StubFactory(new StubHandler(responder)), NullLogger<PageDownloader>.Instance);

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

        var dir = Path.Combine(Path.GetTempPath(), "mangarr-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => downloader.DownloadAsync(OnePage(), dir));
            Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Throws_RateLimitException_On_503_Without_RetryAfter()
    {
        var downloader = DownloaderReturning(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var dir = Path.Combine(Path.GetTempPath(), "mangarr-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => downloader.DownloadAsync(OnePage(), dir));
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

        var dir = Path.Combine(Path.GetTempPath(), "mangarr-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(OnePage(), dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
