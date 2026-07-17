using System.Net;
using Mangarr.Core.Http;
using Mangarr.Core.Sources;
using Microsoft.Extensions.Logging;

namespace Mangarr.Core.Download;

/// <summary>
/// Fetches chapter page images into a working directory with bounded parallelism.
/// Existing files are kept, so a retry only fetches what is missing.
/// </summary>
public class PageDownloader(
    IHttpClientFactory httpClientFactory,
    IDownloadCooldown cooldown,
    ILogger<PageDownloader> logger)
{
    public const string HttpClientName = "pages";
    private const int MaxParallelPerChapter = 4;

    /// <returns>Ordered list of downloaded page file paths.</returns>
    public async Task<List<string>> DownloadAsync(
        ChapterPages pages,
        string workingDir,
        Func<int, int, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(workingDir);
        var client = httpClientFactory.CreateClient(HttpClientName);

        var results = new string[pages.Pages.Count];
        var done = 0;

        await Parallel.ForAsync(0, pages.Pages.Count,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelPerChapter, CancellationToken = ct },
            async (i, token) =>
            {
                var page = pages.Pages[i];
                var extension = ExtensionFor(page.Url);
                var target = Path.Combine(workingDir, $"{i:000}{extension}");
                results[i] = target;

                if (!File.Exists(target))
                {
                    await DownloadPageAsync(client, page, target, token);
                }

                var current = Interlocked.Increment(ref done);
                if (onProgress != null)
                {
                    await onProgress(current, pages.Pages.Count);
                }
            });

        return [.. results];
    }

    private async Task DownloadPageAsync(HttpClient client, PageRequest page, string target, CancellationToken ct)
    {
        // Another download may have tripped the queue-wide backoff after this chapter started.
        // A chapter already in flight is exactly what keeps hammering the source through the
        // cooldown, so honor it per page — not only when a worker picks up its next item.
        await cooldown.WaitAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, page.Url);
        if (page.Headers != null)
        {
            foreach (var (key, value) in page.Headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            throw new RateLimitException(
                $"Rate limited by {request.RequestUri?.Host} (HTTP {(int)response.StatusCode})", retryAfter);
        }

        response.EnsureSuccessStatusCode();

        var temp = target + ".tmp";
        await using (var file = File.Create(temp))
        {
            await response.Content.CopyToAsync(file, ct);
        }

        if (page.ScrambleOffset > 0)
        {
            await MangaFireDescrambler.DescrambleFileAsync(temp, page.ScrambleOffset, ct);
            logger.LogDebug("Descrambled page {Target} (offset {Offset})",
                Path.GetFileName(target), page.ScrambleOffset);
        }

        if (!string.IsNullOrEmpty(page.XorKeyHex))
        {
            await XorDecryptFileAsync(temp, page.XorKeyHex, ct);
            logger.LogDebug("XOR-decrypted page {Target}", Path.GetFileName(target));
        }

        File.Move(temp, target, overwrite: true);
        logger.LogDebug("Downloaded page {Target}", Path.GetFileName(target));
    }

    /// <summary>
    /// XOR-decrypts a file in place with a hex-encoded repeating key. MangaPlus serves
    /// page images this way, handing back the key alongside each page.
    /// </summary>
    private static async Task XorDecryptFileAsync(string path, string hexKey, CancellationToken ct)
    {
        var key = Convert.FromHexString(hexKey);
        if (key.Length == 0)
        {
            return;
        }

        var data = await File.ReadAllBytesAsync(path, ct);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= key[i % key.Length];
        }

        await File.WriteAllBytesAsync(path, data, ct);
    }

    private static string ExtensionFor(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? ".jpg" : extension;
    }
}
