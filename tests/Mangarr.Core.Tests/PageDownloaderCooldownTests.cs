using System.Net;
using Mangarr.Core.Download;
using Mangarr.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mangarr.Core.Tests;

/// <summary>
/// A chapter already being downloaded is what keeps hitting a source after another worker has
/// tripped the queue-wide backoff, so every page must clear the cooldown before it is fetched.
/// </summary>
public class PageDownloaderCooldownTests
{
    private sealed class RecordingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onSend();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9])
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static ChapterPages Pages(int count) =>
        new([.. Enumerable.Range(1, count).Select(i => new PageRequest($"https://example.test/page/{i}.jpg"))]);

    [Fact]
    public async Task Waits_Out_The_Cooldown_Before_Every_Page()
    {
        var cooldown = new FakeCooldown();
        var waitsSeenAtSend = new List<int>();
        var downloader = new PageDownloader(
            new StubFactory(new RecordingHandler(() => waitsSeenAtSend.Add(Volatile.Read(ref cooldown.Waits)))),
            cooldown,
            NullLogger<PageDownloader>.Instance);

        var dir = Path.Combine(Path.GetTempPath(), "mangarr-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            await downloader.DownloadAsync(Pages(3), dir);

            Assert.Equal(3, cooldown.Waits);
            Assert.All(waitsSeenAtSend, seen => Assert.True(seen > 0, "page was fetched before the cooldown was awaited"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cached_Pages_Do_Not_Wait()
    {
        var cooldown = new FakeCooldown();
        var downloader = new PageDownloader(
            new StubFactory(new RecordingHandler(() => { })), cooldown, NullLogger<PageDownloader>.Instance);

        var dir = Path.Combine(Path.GetTempPath(), "mangarr-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            await downloader.DownloadAsync(Pages(2), dir);
            cooldown.Waits = 0;

            // Second pass: both files already exist, so nothing is fetched and nothing waits.
            await downloader.DownloadAsync(Pages(2), dir);

            Assert.Equal(0, cooldown.Waits);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
