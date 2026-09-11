using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Runs on its own five-minute trigger. Reading progress that feeds
/// <see cref="SeriesNeedingTopUpAsync"/> comes from the built-in reader as much as from Kavita, so
/// this must not depend on a scrobble sync having just run, Kavita/tracker-less installs would
/// never top up.
/// <para>
/// The scan is bounded to Smart-monitored series, but it is a query per such series, so it starts
/// with a single existence check: most installs use Smart on nothing at all, and without that check
/// they pay for a settings read and a table scan on every tick forever.
/// </para>
/// <para>
/// This job writes no chapter state. It used to rewrite every <see cref="Chapter.Wanted"/> flag in a
/// series on each top-up so only its current window stayed set, which meant the user's own choices
/// never survived a tick and every count surface read the window instead of the series. The window
/// is now just <see cref="Chapter.NextWanted"/>, evaluated here and thrown away; re-running is safe
/// because <c>DownloadQueueService.EnqueueChapterAsync</c> already drops an already-queued chapter
/// and a downloaded one leaves the candidate set on its own.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class SmartDownloadJob(
    MakiDbContext db,
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    SettingsService settings,
    ILogger<SmartDownloadJob> logger) : IJob
{
    public static readonly JobKey Key = new("smart-download");

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        // Nothing is Smart-monitored, so there is no work this tick and no reason to read settings
        // or walk the table. One indexed-free scan that stops at the first match, against a job that
        // otherwise fires 288 times a day on an install that never opted in.
        if (!await db.Series.AnyAsync(s => s.MonitorNewItems == NewChapterMonitorMode.Smart, ct))
        {
            return;
        }

        var limit = int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersLeft, ct), out var l) ? l : 5;
        var batchSize = int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersCount, ct), out var n) ? n : 10;

        var dueSeries = await SeriesNeedingTopUpAsync(db, limit, ct);

        foreach (var series in dueSeries)
        {
            var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
            var missing = Chapter.NextWanted(chapters, batchSize);

            var queuedItemIds = new List<int>();
            foreach (var chapterId in missing)
            {
                try
                {
                    if (await queue.EnqueueChapterAsync(
                            chapterId, ct, DownloadOrigin.SmartDownload) is { } item)
                    {
                        queuedItemIds.Add(item.Id);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogError(ex, ex.Message);
                }
            }

            batches.Queued(series.Id, series.Title, queuedItemIds, DownloadOrigin.SmartDownload);
            logger.LogInformation(
                "Smart Download queued {Added} chapters for series {SeriesId}", queuedItemIds.Count, series.Id);
        }
    }

    /// <summary>Smart-monitored series that are due for a top-up: reading has caught up to within
    /// <paramref name="limit"/> chapters of what's already downloaded. Skips series with no reading
    /// progress recorded yet or nothing downloaded at all.</summary>
    internal static async Task<List<Series>> SeriesNeedingTopUpAsync(
        MakiDbContext db, int limit, CancellationToken ct)
    {
        var smartSeries = await db.Series
            .Where(s => s.MonitorNewItems == NewChapterMonitorMode.Smart)
            .ToListAsync(ct);

        var due = new List<Series>();
        foreach (var series in smartSeries)
        {
            var downloaded = await db.Chapters.Where(c => c.SeriesId == series.Id && c.ChapterFile != null).ToListAsync(ct);
            // Ordered: a series can carry more than one reading state (two Kavita series can
            // resolve to one local series, and with several accounts every reader owns a row). The
            // question here is "how far ahead of the readers are we" — a union, not a per-user
            // question, because the files are shared and pre-downloading for the furthest reader
            // covers everyone behind them. So the furthest mark across all users is the right one,
            // and no user filter belongs here; this job runs unrestricted on purpose.
            var readStatus = await db.ReadingStates
                .Where(s => s.SeriesId == series.Id)
                .OrderByDescending(s => s.MaxChapter)
                .FirstOrDefaultAsync(ct);
            if (readStatus == null || downloaded.Count == 0)
                continue;

            // Wanted is the whole eligibility rule, so a chapter the user doesn't want must not
            // count as backlog either — otherwise an unwanted special sitting unread would hold the
            // series permanently "not due" and top-ups would stop.
            downloaded = downloaded.Where(c => c.Wanted).ToList();

            var unread = downloaded.Count(c => c.Number > (decimal?)readStatus.MaxChapter);
            if (unread <= limit)
                due.Add(series);
        }

        return due;
    }
}
