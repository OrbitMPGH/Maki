using Mangarr.Api.Services;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Periodically refreshes chapter lists and queues downloads for new monitored chapters. Series
/// are shuffled so one slow or broken source doesn't always starve the same tail of the library.
/// <para>
/// Only a series that is <see cref="SeriesStatus.Completed"/> <em>and</em> already has every
/// chapter MangaBaka knows about is skipped — there is nothing left for it to discover. Anything
/// still running (or of unknown status, or behind MangaBaka's count) is refreshed.
/// </para>
/// <para>
/// Note the asymmetry: "behind MangaBaka" alone is not enough to decide. MangaBaka's total lags
/// the sources on active titles — several series here are already <em>ahead</em> of it (e.g. 195
/// chapters against a reported 187) — so gating purely on that count would stall exactly the
/// ongoing series that need refreshing, until MangaBaka caught back up.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class RefreshMonitoredSeriesJob(
    MangarrDbContext db,
    ChapterSyncService chapterSync,
    DownloadQueueService queue,
    ILogger<RefreshMonitoredSeriesJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        // Compared against the highest chapter number we hold, not the count: sources list
        // specials and one-shots MangaBaka doesn't count, so a count comparison reads as
        // "ahead" (244 vs 240) on a series that is actually exactly in step.
        var seriesIds = await db.Series
            .Where(s => s.SourceMappings.Any(m => m.Enabled))
            .Where(s => s.Status != SeriesStatus.Completed
                        || s.TotalChapters == null
                        || !db.Chapters.Where(c => c.SeriesId == s.Id).Any(c => c.Number >= s.TotalChapters))
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var seriesId in seriesIds.OrderBy(_ => Random.Shared.Next()))
        {
            try
            {
                var newChapterIds = await chapterSync.SyncSeriesAsync(seriesId, ct);
                var monitored = await db.Chapters
                    .Where(c => newChapterIds.Contains(c.Id) && c.Monitored && c.ChapterFileId == null)
                    .Select(c => c.Id)
                    .ToListAsync(ct);

                foreach (var chapterId in monitored)
                {
                    await queue.EnqueueChapterAsync(chapterId, ct);
                }

                if (monitored.Count > 0)
                {
                    logger.LogInformation("Series {SeriesId}: queued {Count} new chapter(s)", seriesId, monitored.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh failed for series {SeriesId}", seriesId);
            }
        }
    }
}
