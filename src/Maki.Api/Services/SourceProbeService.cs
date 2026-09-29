using Maki.Api.Configuration;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Quality;
using Maki.Core.Sources;

namespace Maki.Api.Services;

/// <param name="PageCount">Pages the source lists for the chapter, not the number sampled.</param>
public sealed record ProbeResult(int PageCount, int? MedianWidth, int? MedianHeight,
    string ImageFormat, long SampleBytes, int SampledPages);

/// <summary>
/// Downloads a few pages of one source's copy of a chapter and measures them, so the upgrade scan can
/// judge a candidate without fetching the whole chapter. Shares the download queue's per-source
/// cooldown both ways: a cooling source is not probed, and a 429 here backs downloads off too.
/// </summary>
public sealed class SourceProbeService(
    PageDownloader pageDownloader,
    DownloadQueueService queue,
    AppPaths paths,
    ILogger<SourceProbeService> logger)
{
    /// <summary>Null when the source lists no pages, every sampled page failed, or it rate-limited us.</summary>
    public async Task<ProbeResult?> ProbeAsync(ISource source, string sourceName, SourceChapter chapter,
        int sampleCount, CancellationToken ct)
    {
        if (queue.CooldownRemaining(sourceName) > TimeSpan.Zero)
        {
            return null;
        }

        var dir = Path.Combine(paths.DownloadCacheDir, "probe", Guid.NewGuid().ToString("N"));
        try
        {
            var pages = await source.GetPagesAsync(chapter, ct);
            if (pages.Pages.Count == 0)
            {
                return null;
            }

            var measured = new List<(string Name, byte[] Bytes)>();
            long bytes = 0;
            foreach (var index in SampleIndexes(pages.Pages.Count, sampleCount))
            {
                // One page at a time, each in its own folder: PageDownloader fails the whole batch on
                // one bad page, and a probe only needs most of its sample to say something.
                if (queue.CooldownRemaining(sourceName) > TimeSpan.Zero)
                {
                    return null;
                }

                try
                {
                    var files = await pageDownloader.DownloadAsync(
                        new ChapterPages([pages.Pages[index]]), sourceName,
                        Path.Combine(dir, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
                    var data = await File.ReadAllBytesAsync(files[0], ct);
                    measured.Add((Path.GetFileName(files[0]), data));
                    bytes += data.Length;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && !RateLimitDetector.IsRateLimit(ex, out _))
                {
                    logger.LogDebug(ex, "Probe page {Index} of {Source} chapter {Chapter} failed",
                        index, sourceName, chapter.SourceChapterId);
                }
            }

            if (measured.Count == 0)
            {
                return null;
            }

            var measurement = ChapterFileMeasurer.Measure(measured);
            return new ProbeResult(pages.Pages.Count, measurement.MedianWidth, measurement.MedianHeight,
                measurement.ImageFormat, bytes, measured.Count);
        }
        catch (Exception ex) when (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
        {
            var until = queue.EnterRateLimitCooldown(sourceName, retryAfter);
            logger.LogInformation("Upgrade probe rate-limited by {Source} until {Until:u}", sourceName, until);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Upgrade probe of {Source} chapter {Chapter} failed", sourceName, chapter.SourceChapterId);
            return null;
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// The middle of each of <paramref name="sampleCount"/> equal slices of the chapter, so the sample
    /// covers the whole thing rather than the credits and cover at the front.
    /// </summary>
    public static IReadOnlyList<int> SampleIndexes(int pageCount, int sampleCount)
    {
        if (sampleCount <= 0 || pageCount <= sampleCount)
        {
            return [.. Enumerable.Range(0, pageCount)];
        }

        var indexes = new List<int>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            indexes.Add((int)((2L * i + 1) * pageCount / (2L * sampleCount)));
        }

        return indexes;
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete probe folder {Dir}", dir);
        }
    }
}
