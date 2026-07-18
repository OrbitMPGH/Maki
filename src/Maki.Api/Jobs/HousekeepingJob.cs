using Maki.Api.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>Daily cleanup: orphaned page caches, old finished queue rows, WAL checkpoint.</summary>
[DisallowConcurrentExecution]
public class HousekeepingJob(MakiDbContext db, AppPaths paths, ILogger<HousekeepingJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        // Page caches whose queue item no longer exists or is finished.
        if (Directory.Exists(paths.DownloadCacheDir))
        {
            var activeIds = (await db.DownloadQueue
                    .Where(q => q.Status != QueueStatus.Completed &&
                                q.Status != QueueStatus.Failed &&
                                q.Status != QueueStatus.Cancelled)
                    .Select(q => q.Id)
                    .ToListAsync(ct))
                .Select(id => id.ToString())
                .ToHashSet();

            foreach (var dir in Directory.GetDirectories(paths.DownloadCacheDir))
            {
                if (!activeIds.Contains(Path.GetFileName(dir)))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not delete cache dir {Dir}", dir);
                    }
                }
            }
        }

        // Completed/cancelled queue rows older than 30 days.
        var cutoff = DateTime.UtcNow.AddDays(-30);
        await db.DownloadQueue
            .Where(q => (q.Status == QueueStatus.Completed || q.Status == QueueStatus.Cancelled) &&
                        q.QueuedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
        logger.LogDebug("Housekeeping complete");
    }
}
