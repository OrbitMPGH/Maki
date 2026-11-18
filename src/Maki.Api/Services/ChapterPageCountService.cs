using System.Collections.Concurrent;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public sealed record PageCountRun(int Counted, int Failed, int Skipped, int Waiting);

/// <summary>Chapters whose count or hash failed recently, kept across runs (singleton) so a broken one waits a day.</summary>
public sealed class PageCountFailures
{
    internal ConcurrentDictionary<(int MappingId, string SourceChapterId), DateTimeOffset> Recent { get; } = new();

    /// <summary>The same for page hashes (<see cref="ChapterPageHashService"/>).</summary>
    internal ConcurrentDictionary<(int MappingId, string SourceChapterId), DateTimeOffset> RecentHashes { get; } = new();
}

/// <summary>
/// Fetches the page counts pending chapter folds wait on (<see cref="ChapterFoldPreviewService"/>), one
/// page list per chapter and never an image, and stores them on the link. A run spends at most its
/// budget of requests, the chapters most floors wait on first (a plain chapter that teaches two sources'
/// page scale unblocks every floor between them), then floors spanning more than one source. A source in
/// the download queue's cooldown is passed over; a 429 here puts it in that cooldown for downloads too.
/// A source that needs a browser and cannot count without one (<see cref="IPageCountSource"/>) opens a
/// page per chapter, so it gets at most <see cref="BrowserRequestsPerRun"/>.
/// </summary>
public class ChapterPageCountService(
    MakiDbContext db,
    ChapterFoldPreviewService folds,
    SourceRegistry sourceRegistry,
    DownloadQueueService queue,
    PageCountFailures failures,
    TimeProvider time,
    ILogger<ChapterPageCountService> logger)
{
    public const int RequestsPerRun = 120;

    /// <summary>So one series with a hundred waiting floors does not take a whole run.</summary>
    public const int RequestsPerSeries = 30;

    /// <summary>Holds the source's one browser for about a minute; its syncs and downloads queue behind it.</summary>
    public const int BrowserRequestsPerRun = 20;

    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

    /// <param name="seriesId">
    /// One series only, for the check right after it is matched (<see cref="ChapterFoldChecks"/>): the
    /// budget is then the whole limit, browser sources included, since that series' downloads wait on it.
    /// </param>
    public async Task<PageCountRun> RunAsync(int budget, CancellationToken ct, int? seriesId = null)
    {
        var perSeriesLimit = seriesId is null ? RequestsPerSeries : budget;
        var browserLimit = seriesId is null ? BrowserRequestsPerRun : budget;
        var (floors, _) = await folds.DecideAsync(seriesId, ct);
        var waiting = floors
            .Where(f => f.Missing.Count > 0)
            .SelectMany(f => f.Missing.Select(l => (Link: l,
                Spans: f.Decision.Wholes.Concat(f.Decision.Parts.Keys).Distinct().Count() > 1)))
            .GroupBy(w => (w.Link.MappingId, w.Link.ChapterId))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Any(w => w.Spans))
            .Select(g => g.First().Link)
            .ToList();

        var now = time.GetUtcNow();
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perSeries = new Dictionary<int, int>();
        var perBrowser = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int counted = 0, failed = 0, skipped = 0, spent = 0;
        foreach (var link in waiting)
        {
            if (spent >= budget)
            {
                break;
            }

            if (perSeries.GetValueOrDefault(link.SeriesId) >= perSeriesLimit ||
                blocked.Contains(link.SourceName) ||
                (failures.Recent.TryGetValue((link.MappingId, link.SourceChapterId), out var failedAt) && now - failedAt < RetryFailedAfter))
            {
                skipped++;
                continue;
            }

            if (sourceRegistry.Find(link.SourceName) is not { } source ||
                queue.CooldownRemaining(link.SourceName) > TimeSpan.Zero)
            {
                blocked.Add(link.SourceName);
                skipped++;
                continue;
            }

            if (source is not IPageCountSource && source.Capabilities.HasFlag(SourceCapabilities.NeedsBrowser))
            {
                if (perBrowser.GetValueOrDefault(source.Name) >= browserLimit)
                {
                    blocked.Add(link.SourceName);
                    skipped++;
                    continue;
                }

                perBrowser[source.Name] = perBrowser.GetValueOrDefault(source.Name) + 1;
            }

            spent++;
            perSeries[link.SeriesId] = perSeries.GetValueOrDefault(link.SeriesId) + 1;
            try
            {
                var chapter = link.ToSourceChapter();
                var pages = source is IPageCountSource counter
                    ? await counter.CountPagesAsync(chapter, ct)
                    : (await source.GetPagesAsync(chapter, ct)).Pages.Count;
                if (pages <= 0)
                {
                    failures.Recent[(link.MappingId, link.SourceChapterId)] = now;
                    failed++;
                    continue;
                }

                await db.ChapterSourceLinks
                    .Where(l => l.ChapterId == link.ChapterId && l.SourceMappingId == link.MappingId &&
                                l.SourceChapterId == link.SourceChapterId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.PageCount, pages), ct);
                counted++;
            }
            catch (Exception ex) when (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
            {
                var until = queue.EnterRateLimitCooldown(link.SourceName, retryAfter);
                logger.LogInformation("Page counting rate-limited by {Source} until {Until:u}", link.SourceName, until);
                blocked.Add(link.SourceName);
                failed++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Could not count the pages of {Source} chapter {Chapter}", link.SourceName, link.SourceChapterId);
                failures.Recent[(link.MappingId, link.SourceChapterId)] = now;
                failed++;
            }
        }

        if (counted + failed > 0)
        {
            logger.LogInformation(
                "Counted pages for {Counted} chapters ({Failed} failed, {Skipped} passed over, {Waiting} waiting in all)",
                counted, failed, skipped, waiting.Count);
        }

        return new PageCountRun(counted, failed, skipped, waiting.Count);
    }
}
