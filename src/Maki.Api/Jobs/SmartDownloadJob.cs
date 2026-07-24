using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Chained after <see cref="ScrobbleJob"/>, which runs on an every-minute tick regardless of
/// whether a sync actually happened (<see cref="ScrobbleService.TickAsync"/> no-ops when the
/// configured interval hasn't elapsed). Bail out unless a sync just completed, so this doesn't
/// scan every Smart-monitored series once a minute for nothing.
/// </summary>
[DisallowConcurrentExecution]
public class SmartDownloadJob(
    MakiDbContext db,
    DownloadQueueService queue,
    SettingsService settings,
    ScrobbleService scrobbler,
    ILogger<SmartDownloadJob> logger) : IJob
{
    public static readonly JobKey Key = new("smart-download");

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var lastSync = await scrobbler.LastSyncAtAsync(ct);
        if (lastSync is not { } at || DateTime.UtcNow - at > TimeSpan.FromMinutes(1))
        {
            return;
        }

        var limit = int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersLeft, ct), out var l) ? l : 5;
        var skipSpecials = await settings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true";

        var smartSeries = await db.Series
            .Where(s => s.MonitorNewItems == NewChapterMonitorMode.Smart)
            .ToListAsync(ct);

        foreach (var series in smartSeries)
        {
            var downloaded = await db.Chapters.Where(c => c.SeriesId == series.Id && c.ChapterFile != null).ToListAsync(ct);
            var readStatus = await db.ReadingStates.FirstOrDefaultAsync(s => s.SeriesId == series.Id, ct);
            if (readStatus == null || downloaded.Count == 0)
                continue;
            
            if (skipSpecials)
                downloaded = downloaded.Where(c => !Chapter.IsSpecial(c.Number)).ToList();

            var unread = downloaded.Count(c => c.Number > (decimal?)readStatus.MaxChapter);
            if (unread > limit)
                continue;

            var missing = await MonitorSmart(series.Id, db, settings, ct);

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

            logger.LogInformation("Smart Download queued {Added} chapters for series {SeriesId}", added, series.Id);
        }
    }

    private static async Task<HashSet<int>> MonitorSmart(int seriesId, MakiDbContext db, SettingsService settings, CancellationToken ct)
    {
        var chapters = await db.Chapters.Where(c => c.SeriesId == seriesId).ToListAsync(ct);
        var monitorSmart = await MonitorSmart(chapters, settings, ct);
        await db.SaveChangesAsync(ct);
        return monitorSmart;
    }

    /// <summary>Caps monitoring to the next batch of undownloaded chapters; unmonitors everything
    /// else so switching to Smart mode from All/MainOnly actually shrinks what's monitored.</summary>
    internal static async Task<HashSet<int>> MonitorSmart(List<Chapter> chapters, IAppSettings settings, CancellationToken ct)
    {
        var smartChapterCount = int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersCount, ct), out var n) ? n : 10;
        var skipSpecials = await settings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true";

        var downloadedIds = chapters.Where(c => c.ChapterFileId != null).Select(c => c.Id).ToHashSet();
        var missing = chapters
            .Where(c => !downloadedIds.Contains(c.Id) && (!skipSpecials || !Chapter.IsSpecial(c.Number)))
            .Take(smartChapterCount)
            .ToList();
        var missingIds = missing.Select(c => c.Id).ToHashSet();

        foreach (var chapter in chapters)
        {
            chapter.Monitored = missingIds.Contains(chapter.Id) || chapter.ChapterFileId != null;
        }

        return missingIds;
    }
}
