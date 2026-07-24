using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

[DisallowConcurrentExecution]
public class SmartDownloadJob(
    MakiDbContext db,
    DownloadQueueService queue,
    SettingsService settings,
    ILogger<SmartDownloadJob> logger) : IJob
{
    public static readonly JobKey Key = new("smart-download");
    
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var limit = int.Parse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersLeft, ct) ?? "5");

        var smartSeries = await db.Series
            .Where(s => s.MonitorNewItems == NewChapterMonitorMode.Smart)
            .ToListAsync(ct);

        foreach (var series in smartSeries)
        {
            var downloaded = await db.Chapters.Where(c => c.SeriesId == series.Id && c.ChapterFile != null).ToListAsync(ct);
            var readStatus = await db.ReadingStates.FirstOrDefaultAsync(s => s.Id == series.Id, ct);
            if (readStatus == null || downloaded.Count == 0)
                continue;
            
            var unread = downloaded.Count(c => c.Number > (decimal?)readStatus.MaxChapter);
            if (unread > limit)
                continue;

            var missing= await MonitorSmart(series.Id, db, settings, ct);

            var added = 0;
            foreach (var chapterId in missing)
            {
                try
                {
                    await queue.EnqueueChapterAsync(chapterId, ct);
                    added++;
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogError(ex, ex.Message);    
                }
            }
            
            logger.LogInformation("Queued {Added} chapters for series {SeriesId}", added, series.Id);
        }
    }

    private static async Task<List<int>> MonitorSmart(int seriesId, MakiDbContext db, SettingsService settings, CancellationToken ct)
    {
        var chapters = await db.Chapters.Where(c => c.SeriesId == seriesId).ToListAsync(ct);
        var monitorSmart = await MonitorSmart(chapters, settings, ct);
        await db.SaveChangesAsync(ct);
        return monitorSmart;
    }
    
    internal static async Task<List<int>> MonitorSmart(List<Chapter> chapters, IAppSettings settings, CancellationToken ct)
    {
        var smartChapterCount = int.Parse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersCount, ct) ?? "10");
        var skipSpecials = await settings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true";
        var downloaded = chapters.Where(c => c.ChapterFileId != null).ToList();
        var missing =  chapters.Where(c => !skipSpecials || !Chapter.IsSpecial(c.Number)).Except(downloaded).Take(smartChapterCount).ToList();
        foreach (var chapter in missing)
        {
            chapter.Monitored = true;
        }
        return missing.Select(chapter => chapter.Id).ToList();
    }
}