using System.Collections.Concurrent;
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
/// The scan is bounded to Smart-monitored series and takes three queries however many there are.
/// It still starts with a single existence check: most installs use Smart on nothing at all, and
/// without that check they pay for a settings read and the scan on every tick forever.
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

    // Series already reported as skipped, with why. The job is built per run, so this is static: a
    // series that stays unmapped would otherwise be warned about every five minutes.
    private static readonly ConcurrentDictionary<int, string> Skipped = new();

    public const int MinChapters = 1, MaxChaptersLeft = 10, MaxChaptersPerBatch = 20;

    /// <summary>A stored 0 (from before the save was validated) would queue nothing, so reads clamp it.</summary>
    public static int ClampChaptersLeft(int value) => Math.Clamp(value, MinChapters, MaxChaptersLeft);

    public static int ClampBatchSize(int value) => Math.Clamp(value, MinChapters, MaxChaptersPerBatch);

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

        var limit = ClampChaptersLeft(
            int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersLeft, ct), out var l) ? l : 5);
        var batchSize = ClampBatchSize(
            int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersCount, ct), out var n) ? n : 10);
        var maxAttempts = int.TryParse(await settings.GetAsync(SettingKeys.DownloadRetryMaxAttempts, ct), out var m)
            ? m
            : 5;

        var dueSeries = await SeriesNeedingTopUpAsync(db, limit, ct);

        foreach (var (series, after) in dueSeries)
        {
            var chapters = await db.Chapters.AsNoTracking().Where(c => c.SeriesId == series.Id).ToListAsync(ct);
            var backingOff = await BackingOffAsync(db, chapters, maxAttempts, DateTime.UtcNow, ct);
            var missing = Chapter.NextWanted(Ahead(chapters, after), batchSize, backingOff);

            if (missing.Count == 0)
            {
                continue;
            }

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
                catch (EnqueueRefusedException ex) when (ex.Key is EnqueueRefusedException.NoMapping or EnqueueRefusedException.HealthReview)
                {
                    if (Skipped.TryGetValue(series.Id, out var reported) && reported == ex.Key)
                    {
                        logger.LogDebug("Smart Download still skipping series {SeriesId}: {Reason}", series.Id, ex.Key);
                    }
                    else
                    {
                        Skipped[series.Id] = ex.Key;
                        logger.LogWarning("Smart Download skipped series {SeriesId}: {Reason}", series.Id, ex.Key);
                    }

                    break;
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(ex, "Smart Download could not queue chapter {ChapterId} of series {SeriesId}", chapterId, series.Id);
                }
            }

            if (queuedItemIds.Count == 0)
            {
                continue;
            }

            Skipped.TryRemove(series.Id, out _);
            await batches.QueuedAsync(series.Id, series.Title, queuedItemIds, DownloadOrigin.SmartDownload);
            logger.LogInformation(
                "Smart Download queued {Added} chapters for series {SeriesId}", queuedItemIds.Count, series.Id);
        }
    }

    /// <summary>
    /// Wanted, missing chapters whose latest queue row failed and is not due again: still inside its
    /// retry backoff, or out of attempts. Enqueueing only dedupes against active rows, so without this
    /// every tick queued a fresh row for a failing chapter, bypassing both the backoff and the cap,
    /// and a chapter no source carries held its place in the window forever.
    /// </summary>
    internal static async Task<HashSet<int>> BackingOffAsync(
        MakiDbContext db, IEnumerable<Chapter> chapters, int maxAttempts, DateTime now, CancellationToken ct)
    {
        var candidates = chapters.Where(Chapter.IsMissing).Select(c => c.Id).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var rows = await db.DownloadQueue
            .Where(q => q.ChapterId != null && candidates.Contains(q.ChapterId.Value))
            .Select(q => new { ChapterId = q.ChapterId!.Value, q.Id, q.Status, q.RetryCount, q.NextAttempt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ChapterId)
            .Select(g => g.MaxBy(r => r.Id)!)
            .Where(r => r.Status == QueueStatus.Failed && (r.RetryCount >= maxAttempts || r.NextAttempt > now))
            .Select(r => r.ChapterId)
            .ToHashSet();
    }

    /// <summary>
    /// The chapters past the reader's position. A batch starts there rather than at the lowest missing
    /// chapter: a series read up to 40 on a tracker with nothing on disk would otherwise download
    /// 1-10, which nobody is going to read. One-shots have no number and stay in, sorted last.
    /// </summary>
    internal static IEnumerable<Chapter> Ahead(IEnumerable<Chapter> chapters, decimal? after) =>
        after is { } position ? chapters.Where(c => c.Number is null || c.Number > position) : chapters;

    internal sealed record TopUp(Series Series, decimal? After);

    /// <summary>
    /// Smart-monitored series due for a top-up, with the position the batch starts after.
    /// <list type="bullet">
    /// <item>Nobody has read it yet: due only while nothing wanted is on disk, so a new series gets
    /// its first batch on its own and then waits for someone to start reading.</item>
    /// <item>Someone has: due once the downloaded, unread chapters past the furthest reader number
    /// <paramref name="limit"/> or fewer, including none at all.</item>
    /// </list>
    /// </summary>
    internal static async Task<List<TopUp>> SeriesNeedingTopUpAsync(
        MakiDbContext db, int limit, CancellationToken ct)
    {
        var smartSeries = await db.Series
            .AsNoTracking()
            .Where(s => s.MonitorNewItems == NewChapterMonitorMode.Smart)
            .ToListAsync(ct);
        if (smartSeries.Count == 0)
        {
            return [];
        }

        var ids = smartSeries.Select(s => s.Id).ToList();

        // Wanted is the whole eligibility rule, so a chapter the user doesn't want must not count as
        // backlog either, or an unwanted special sitting unread would hold the series permanently
        // "not due" and top-ups would stop.
        var downloaded = (await db.Chapters
                .Where(c => ids.Contains(c.SeriesId) && c.ChapterFileId != null && c.Wanted)
                .Select(c => new { c.SeriesId, c.Number })
                .ToListAsync(ct))
            .ToLookup(c => c.SeriesId, c => c.Number);

        // The furthest mark across every reader: the files are shared, and pre-downloading for the
        // furthest reader covers everyone behind them. A series can also carry more than one state
        // (two Kavita series resolving to one local series). No user filter belongs here; this job
        // runs unrestricted on purpose.
        var positions = await db.ReadingStates
            .Where(s => s.SeriesId != null && ids.Contains(s.SeriesId.Value))
            .GroupBy(s => s.SeriesId!.Value)
            .Select(g => new { SeriesId = g.Key, Max = g.Max(s => s.MaxChapter) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Max, ct);

        var due = new List<TopUp>();
        foreach (var series in smartSeries)
        {
            var onDisk = downloaded[series.Id].ToList();
            if (!positions.TryGetValue(series.Id, out var max))
            {
                if (onDisk.Count == 0)
                {
                    due.Add(new TopUp(series, null));
                }

                continue;
            }

            var after = (decimal)max;
            if (onDisk.Count(n => n > after) <= limit)
            {
                due.Add(new TopUp(series, after));
            }
        }

        return due;
    }
}
