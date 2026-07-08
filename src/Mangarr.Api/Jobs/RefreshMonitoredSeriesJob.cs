using Mangarr.Api.Services;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Periodically refreshes chapter lists for monitored series and queues
/// downloads for new monitored chapters. Series are shuffled so one slow or
/// broken source doesn't always starve the same tail of the library.
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
        var seriesIds = await db.Series
            .Where(s => s.Monitored && s.SourceMappings.Any(m => m.Enabled))
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
