using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>Daily cleanup: orphaned page caches, old finished queue rows, WAL checkpoint, SQLite pool.</summary>
[DisallowConcurrentExecution]
public class HousekeepingJob(
    MakiDbContext db, AppPaths paths, UpgradeTrashService upgradeTrash, ILogger<HousekeepingJob> logger) : IJob
{
    /// <summary>Most in-app notifications kept per user, read or not. Well past what anyone scrolls.</summary>
    private const int InboxCap = 200;

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
                if (ct.IsCancellationRequested)
                {
                    // Shutdown mid-sweep. Every section here is independent and idempotent,
                    // so the next run picks up whatever this one did not get to.
                    return;
                }

                // Upgrade probes scratch under here and clean up after themselves; one still running
                // must not lose its folder mid-download.
                if (Path.GetFileName(dir) == "probe" && Directory.GetLastWriteTimeUtc(dir) > DateTime.UtcNow.AddHours(-1))
                {
                    continue;
                }

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
                if (ct.IsCancellationRequested)
                {
                    // Shutdown mid-sweep. Every section here is independent and idempotent,
                    // so the next run picks up whatever this one did not get to.
                    return;
                }

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

        // Source-comparison samples and Discover previews. Each job clears its own folder when it
        // is superseded, so anything still here belongs to one somebody looked at and closed.
        foreach (var previewRoot in new[] { paths.SourcePreviewDir, paths.SeriesPreviewDir }.Where(Directory.Exists))
        {
            var stale = DateTime.UtcNow.AddDays(-1);
            foreach (var dir in Directory.GetDirectories(previewRoot))
            {
                if (ct.IsCancellationRequested)
                {
                    // Shutdown mid-sweep. Every section here is independent and idempotent,
                    // so the next run picks up whatever this one did not get to.
                    return;
                }

                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < stale)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not clean preview dir {Dir}", dir);
                }
            }
        }

        try
        {
            var purged = await upgradeTrash.PurgeAsync(ct);
            if (purged > 0)
            {
                logger.LogInformation("Purged {Count} replaced chapter file(s) from upgrade trash", purged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Upgrade trash purge failed");
        }

        // Completed/cancelled queue rows older than 30 days.
        var cutoff = DateTime.UtcNow.AddDays(-30);
        await db.DownloadQueue
            .Where(q => (q.Status == QueueStatus.Completed || q.Status == QueueStatus.Cancelled) &&
                        q.QueuedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        await PruneInboxAsync(ct);

        // 0x10002: consider every table, not only the ones this pooled connection happened to query.
        await db.Database.ExecuteSqlRawAsync("PRAGMA optimize=0x10002;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);

        // Microsoft.Data.Sqlite pools native handles per connection string with no upper bound, and
        // each one keeps SQLite's own page cache (~2 MB by default) alive behind it. The pool
        // therefore grows to the highest concurrency the process ever saw - a scan overlapping a
        // download burst overlapping a request - and never gives any of it back. Clearing it daily
        // makes that a sawtooth instead of a ratchet. Connections currently in use are untouched;
        // they return to an empty pool. Nothing else in the app is affected: every other SQLite
        // consumer here opens with Pooling=False so the nightly artifact swaps can replace a file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        logger.LogDebug("Housekeeping complete");
    }

    /// <summary>
    /// Two rules for the in-app notification inbox, because either alone leaks:
    /// <list type="bullet">
    /// <item>read rows older than 30 days go, since a notification the user acknowledged a month ago
    /// is history nobody asked to keep;</item>
    /// <item>everything past the newest <see cref="InboxCap"/> per user goes too, <b>unread
    /// included</b>. Somebody who never opens the bell would otherwise accumulate a row per automatic
    /// download forever, and an age rule alone never touches them.</item>
    /// </list>
    /// <para>
    /// Runs unrestricted (this job has no user), so the cap is applied per user by a window function
    /// rather than by walking accounts — one statement instead of a query per account.
    /// </para>
    /// </summary>
    private async Task PruneInboxAsync(CancellationToken ct)
    {
        var inboxCutoff = DateTime.UtcNow.AddDays(-30);
        var aged = await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(n => n.ReadAt != null && n.CreatedAt < inboxCutoff)
            .ExecuteDeleteAsync(ct);

        var capped = await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM UserNotifications
            WHERE Id IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (
                        PARTITION BY UserId ORDER BY CreatedAt DESC, Id DESC) AS rn
                    FROM UserNotifications
                )
                WHERE rn > {0}
            );
            """,
            [InboxCap], ct);

        if (aged + capped > 0)
        {
            logger.LogDebug("Pruned {Aged} aged and {Capped} over-cap inbox notification(s)", aged, capped);
        }
    }
}
