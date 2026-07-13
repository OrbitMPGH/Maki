using System.Net;
using Mangarr.Core.Http;
using Mangarr.Core.Sources;
using Microsoft.Extensions.Logging;

namespace Mangarr.Core.Download;

/// <summary>
/// Fetches chapter page images into a working directory with bounded parallelism.
/// Existing files are kept, so a retry only fetches what is missing.
/// </summary>
public class PageDownloader(IHttpClientFactory httpClientFactory, ILogger<PageDownloader> logger)
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

        File.Move(temp, target, overwrite: true);
        logger.LogDebug("Downloaded page {Target}", Path.GetFileName(target));
    }

    private static string ExtensionFor(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? ".jpg" : extension;
    }
}
