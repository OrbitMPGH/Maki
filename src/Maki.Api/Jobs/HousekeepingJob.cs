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

        // Reader thumbnails. Regenerable on demand, so anything doubtful is safe to delete.
        // Two kinds of garbage, and only the first used to be collected:
        //   1. whole directories for ChapterFile rows that no longer exist;
        //   2. files inside a *live* directory left by an earlier version of the same archive —
        //      the name is "{ArchiveSize}-{page}.jpg", so a re-download at a different size
        //      orphans every thumbnail it had without the directory ever going away.
        if (Directory.Exists(paths.ReaderCacheDir))
        {
            var sizeByFileId = await db.ChapterFiles
                .Select(f => new { f.Id, f.Size })
                .ToDictionaryAsync(f => f.Id.ToString(), f => f.Size.ToString(), ct);

            foreach (var dir in Directory.GetDirectories(paths.ReaderCacheDir))
            {
                try
                {
                    if (!sizeByFileId.TryGetValue(Path.GetFileName(dir), out var currentSize))
                    {
                        Directory.Delete(dir, recursive: true);
                        continue;
                    }

                    var prefix = currentSize + "-";
                    foreach (var thumb in Directory.GetFiles(dir, "*.jpg"))
                    {
                        if (!Path.GetFileName(thumb).StartsWith(prefix, StringComparison.Ordinal))
                        {
                            File.Delete(thumb);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not clean reader cache dir {Dir}", dir);
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
