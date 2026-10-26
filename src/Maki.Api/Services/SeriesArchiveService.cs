using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using Maki.Core.Paths;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum SeriesArchiveStatus
{
    Ok,
    NotFound,

    /// <summary>The series' state does not allow it right now (409).</summary>
    Conflict,

    /// <summary>The request cannot work for this series at all (400).</summary>
    Invalid,

    /// <summary>The recycle bin cannot take the files; the caller may offer a permanent delete.</summary>
    BinRefused
}

/// <summary>What an archive, file removal or restore did, or the catalogue key saying why it did not.</summary>
public sealed record SeriesArchiveResult(SeriesArchiveStatus Status, string? ErrorKey = null)
{
    public int FilesDeleted { get; init; }
    public int FilesFailed { get; init; }

    /// <summary>Restore only: removed chapters queued to download again.</summary>
    public int Queued { get; init; }

    /// <summary>Restore only: a catalogue key when some of those chapters could not be queued.</summary>
    public string? QueueError { get; init; }

    /// <summary>
    /// Restore only: the source refresh started off the request. Request callers never wait on it;
    /// it is here for tests and for callers that run outside a request.
    /// </summary>
    public Task? Refresh { get; init; }

    public bool Succeeded => Status == SeriesArchiveStatus.Ok;

    internal static readonly SeriesArchiveResult NotFound = new(SeriesArchiveStatus.NotFound);
    internal static SeriesArchiveResult Conflict(string key) => new(SeriesArchiveStatus.Conflict, key);
    internal static SeriesArchiveResult Invalid(string key) => new(SeriesArchiveStatus.Invalid, key);
    internal static SeriesArchiveResult BinRefused(string key) => new(SeriesArchiveStatus.BinRefused, key);
}

/// <summary>
/// Moves a series out of the library and back. An archived series keeps every row (chapters,
/// progress, ratings, mappings) and stops talking to sources and the download queue; see the
/// guards on <see cref="Series.ArchivedAt"/>. Everything here runs under the series lock, and the
/// order inside it matters: refuse while anything could still write into the series, clear the
/// queue, then flag it, and only then touch files.
/// </summary>
public class SeriesArchiveService(
    MakiDbContext db,
    ChapterFileDeletion deletion,
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    ReaderArchiveCache archives,
    CoverService covers,
    NotificationService notifications,
    IMessageCatalog catalog,
    IUserLocaleResolver locales,
    ICurrentUser currentUser,
    AppPaths paths,
    TimeProvider time,
    IServiceScopeFactory scopeFactory,
    ILogger<SeriesArchiveService> logger,
    IHostApplicationLifetime? lifetime = null)
{
    public async Task<SeriesArchiveResult> ArchiveAsync(
        int seriesId, bool removeFiles, bool permanent, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return SeriesArchiveResult.NotFound;
        }

        Series series;
        SeriesArchiveResult result;
        using (await SeriesLocks.SeriesAsync(seriesId, ct))
        {
            var loaded = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
            if (loaded is null)
            {
                return SeriesArchiveResult.NotFound;
            }

            series = loaded;
            if (series.ArchivedAt is not null)
            {
                return SeriesArchiveResult.Conflict("error.series.archived");
            }

            // A worker, an automatic torrent import, a running source match or a pending import link
            // would write into the series after it is archived.
            if (series.SourceMatchPending || series.PendingImportLink != PendingImportLink.None ||
                await SeriesLocks.InFlight(db.DownloadQueue).AnyAsync(q => q.SeriesId == seriesId, ct))
            {
                return SeriesArchiveResult.Conflict("error.series.activeDownloadArchive");
            }

            var files = await db.ChapterFiles.Where(f => f.SeriesId == seriesId).OrderBy(f => f.Id).ToListAsync(ct);

            // Probed before anything changes, so a refusal leaves the series in the library and the
            // client can ask again with permanent set.
            if (removeFiles && RemovalRefusal(series, files, permanent) is { } refused)
            {
                return refused;
            }

            // From here the request no longer decides: a drop between the queue sweep and the save
            // below would fire the batch summary and leave the rows Queued.
            ct.ThrowIfCancellationRequested();

            // A health repair of a kept file stays allowed, and its operation only settles once its
            // rows do, so those rows are left to finish.
            await QueueItemCancellation.CancelAsync(db, queue, batches,
                q => q.SeriesId == seriesId && q.HealthOperationId == null, CancellationToken.None);

            var now = time.GetUtcNow().UtcDateTime;
            int? userId = currentUser.UserId > 0 ? currentUser.UserId : null;
            var proposals = await db.TorrentProposals
                .Where(p => p.SeriesId == seriesId && p.Status == TorrentProposalStatus.Pending)
                .ToListAsync(CancellationToken.None);
            foreach (var proposal in proposals)
            {
                proposal.Status = TorrentProposalStatus.Dismissed;
                proposal.ResolvedByUserId = userId;
                proposal.ResolvedAtUtc = now;
            }

            series.ArchivedAt = now;
            series.ArchivedByUserId = userId;
            await db.SaveChangesAsync(CancellationToken.None);

            // The series is archived from here on, so a dropped request must not stop halfway.
            result = new SeriesArchiveResult(SeriesArchiveStatus.Ok);
            if (removeFiles)
            {
                var removed = await RemoveFilesLockedAsync(series, files, permanent, CancellationToken.None);
                result = result with { FilesDeleted = removed.Deleted + removed.Kept, FilesFailed = removed.Failed };
            }

            EvictCaches(files);
        }

        await NotifyArchivedAsync(series, removeFiles, permanent);
        return result;
    }

    /// <summary>
    /// <see cref="ArchiveAsync"/> per id, one lock at a time. A refused id does not stop the rest.
    /// </summary>
    public async Task<IReadOnlyList<(int SeriesId, SeriesArchiveResult Result)>> ArchiveManyAsync(
        IEnumerable<int> seriesIds, bool removeFiles, bool permanent, CancellationToken ct)
    {
        var results = new List<(int, SeriesArchiveResult)>();
        foreach (var id in seriesIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            results.Add((id, await ArchiveAsync(id, removeFiles, permanent, ct)));

            // Each series loads its chapters and files; a long list must not drag them all along.
            db.ChangeTracker.Clear();
        }

        return results;
    }

    /// <summary>Removes the files of a series that was archived with its files kept.</summary>
    public async Task<SeriesArchiveResult> RemoveFilesAsync(int seriesId, bool permanent, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return SeriesArchiveResult.NotFound;
        }

        using var seriesLock = await SeriesLocks.SeriesAsync(seriesId, ct);
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return SeriesArchiveResult.NotFound;
        }

        if (series.ArchivedAt is null)
        {
            return SeriesArchiveResult.Conflict("error.series.notArchived");
        }

        // Only a running worker or import is refused here. A queued health repair stages its copy
        // under .maki/health and puts nothing in the series folder until an admin applies it.
        if (await SeriesLocks.InFlight(db.DownloadQueue).AnyAsync(q => q.SeriesId == seriesId, ct))
        {
            return SeriesArchiveResult.Conflict("error.series.activeDownloadDelete");
        }

        var files = await db.ChapterFiles.Where(f => f.SeriesId == seriesId).OrderBy(f => f.Id).ToListAsync(ct);
        if (RemovalRefusal(series, files, permanent) is { } refused)
        {
            return refused;
        }

        var removed = await RemoveFilesLockedAsync(series, files, permanent, ct);
        EvictCaches(files);
        return new SeriesArchiveResult(SeriesArchiveStatus.Ok)
        {
            FilesDeleted = removed.Deleted + removed.Kept,
            FilesFailed = removed.Failed
        };
    }

    /// <summary>
    /// Puts an archived series back in the library with the monitoring mode it had, and refreshes it
    /// off the request. <see cref="Series.MonitorNewItems"/> is never touched by archive or restore.
    /// </summary>
    /// <param name="redownloadRemoved">
    /// Queue the chapters whose files were removed and that are still wanted. Goes straight to the
    /// queue, past the refresh's burst hold: the user asked for exactly these and saw the count.
    /// </param>
    public async Task<SeriesArchiveResult> RestoreAsync(int seriesId, bool redownloadRemoved, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return SeriesArchiveResult.NotFound;
        }

        var result = new SeriesArchiveResult(SeriesArchiveStatus.Ok);
        using (await SeriesLocks.SeriesAsync(seriesId, ct))
        {
            var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
            if (series is null)
            {
                return SeriesArchiveResult.NotFound;
            }

            if (series.ArchivedAt is null)
            {
                return SeriesArchiveResult.Conflict("error.series.notArchived");
            }

            series.ArchivedAt = null;
            series.ArchivedByUserId = null;
            series.NextCheckAt = null;
            await db.SaveChangesAsync(ct);

            if (redownloadRemoved)
            {
                // Clearing the stamp is what makes them missing again; SetWantedBulk only does that on
                // a false to true flip, and these are already wanted.
                var removed = await db.Chapters
                    .Where(c => c.SeriesId == seriesId && c.ChapterFileId == null && c.FileRemovedAt != null && c.Wanted)
                    .ToListAsync(CancellationToken.None);
                foreach (var chapter in removed)
                {
                    chapter.FileRemovedAt = null;
                }

                await db.SaveChangesAsync(CancellationToken.None);

                var chapterIds = removed
                    .OrderBy(c => c.Number ?? decimal.MaxValue)
                    .ThenBy(c => c.Id)
                    .Select(c => c.Id)
                    .ToList();
                if (chapterIds.Count > 0)
                {
                    var enqueued = await queue.EnqueueChaptersAsync(
                        chapterIds, DownloadOrigin.Manual, currentUser.UserId > 0 ? currentUser.UserId : null,
                        CancellationToken.None);
                    var itemIds = enqueued.Queued.Select(item => item.Id).ToList();
                    await batches.QueuedAsync(seriesId, series.Title, itemIds);
                    result = result with { Queued = itemIds.Count, QueueError = enqueued.Error };
                }
            }
        }

        return result with { Refresh = StartRefresh(seriesId) };
    }

    /// <summary>How long ago another reader's unfinished progress still counts as "reading it now".</summary>
    public static readonly TimeSpan ActiveReaderWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// What removing the series' files would free: the recorded size of every file that is not
    /// hardlinked to something else, since a seeded copy keeps the bytes alive.
    /// </summary>
    public static async Task<long> FreeableBytesAsync(MakiDbContext db, Series series, CancellationToken ct)
    {
        if (series.RootFolder is null)
        {
            return 0;
        }

        var files = await db.ChapterFiles.AsNoTracking()
            .Where(f => f.SeriesId == series.Id)
            .Select(f => new { f.RelativePath, f.Size })
            .ToListAsync(ct);
        return files.Sum(f =>
            LibraryPaths.ResolveForDelete(series.RootFolder.Path, f.RelativePath) is { } path && HardLinks.IsShared(path)
                ? 0
                : f.Size);
    }

    /// <summary>
    /// Other users with unfinished progress on the series inside <see cref="ActiveReaderWindow"/>.
    /// Counts people only; reading is private, so nothing identifies them.
    /// </summary>
    public static Task<int> OtherActiveReadersAsync(
        MakiDbContext db, int seriesId, int userId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc - ActiveReaderWindow;
        return db.ChapterProgress.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.SeriesId == seriesId && p.UserId != userId && !p.Completed && p.UnreadAt == null &&
                        p.UpdatedAt >= since)
            .Select(p => p.UserId)
            .Distinct()
            .CountAsync(ct);
    }

    private SeriesArchiveResult? RemovalRefusal(Series series, IReadOnlyList<ChapterFile> files, bool permanent)
    {
        if (files.Count == 0)
        {
            return null;
        }

        if (series.RootFolder is null)
        {
            return SeriesArchiveResult.Invalid("error.series.noRootFolder");
        }

        if (permanent)
        {
            return null;
        }

        var targets = files
            .Select(f => LibraryPaths.ResolveForDelete(series.RootFolder.Path, f.RelativePath))
            .OfType<string>();
        return deletion.Bin.Refusal(series.RootFolder.Path, targets) is { } key
            ? SeriesArchiveResult.BinRefused(key)
            : null;
    }

    /// <summary>
    /// Deletes every file of the series through <see cref="ChapterFileDeletion"/>, which stamps
    /// <see cref="Chapter.FileRemovedAt"/> on each chapter and leaves Wanted alone, then prunes the
    /// emptied folders bottom-up the way series delete does. The caller holds the series lock.
    /// </summary>
    private async Task<ChapterFileDeletion.Result> RemoveFilesLockedAsync(
        Series series, IReadOnlyList<ChapterFile> files, bool permanent, CancellationToken ct)
    {
        if (files.Count == 0 || series.RootFolder is null)
        {
            return new ChapterFileDeletion.Result(0, 0, 0, 0);
        }

        // Read while the rows that name the folders still exist.
        var shared = await SeriesFolders.SharedAsync(db, series, ct);
        var extraFolders = (await SeriesFolders.ForAsync(db, series, ct)).Skip(1).ToList();

        var removed = await deletion.DeleteAsync(
            series, files, ct, binReason: permanent ? null : RecycleReason.SeriesArchive, automatic: false);

        PruneFolders(series, shared, extraFolders);
        return removed;
    }

    private void PruneFolders(Series series, bool shared, IReadOnlyList<string> extraFolders)
    {
        var rootPath = series.RootFolder!.Path;
        void Attempt(string path, Action prune)
        {
            try
            {
                prune();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not prune {Path} for archived series {SeriesId}", path, series.Id);
            }
        }

        if (LibraryPaths.ResolveNoLinks(rootPath, series.FolderName) is { } folder && Directory.Exists(folder))
        {
            if (shared)
            {
                Attempt(folder, () => SeriesFolders.RemoveIfEmpty(folder));
            }
            else
            {
                Attempt(folder, () =>
                {
                    var cover = Path.Combine(folder, "cover.jpg");
                    var makiCover = covers.IsMakiLibraryCover(series.Id, cover) ? cover : null;
                    if (SeriesFolders.PruneTree(folder, makiCover) is var left and > 0)
                    {
                        logger.LogInformation("Kept {Folder} for archived series {SeriesId}: {Count} entries are still in it",
                            folder, series.Id, left);
                    }
                });
            }
        }

        foreach (var extra in extraFolders)
        {
            if (LibraryPaths.ResolveNoLinks(rootPath, extra) is { } dir)
            {
                Attempt(dir, () => SeriesFolders.RemoveIfEmpty(dir));
            }
        }
    }

    private void EvictCaches(IReadOnlyList<ChapterFile> files)
    {
        foreach (var file in files)
        {
            archives.Invalidate(file.Id);
        }

        ReaderPageCache.Evict(paths, files.Select(f => f.Id));
    }

    private async Task NotifyArchivedAsync(Series series, bool removeFiles, bool permanent)
    {
        try
        {
            var locale = await locales.DefaultAsync(CancellationToken.None);
            notifications.Dispatch(NotificationEventType.SeriesArchived, new NotificationMessage(
                NotificationEventType.SeriesArchived,
                Title: catalog.GetFor(locale, "notify.series.archived.title"),
                Body: catalog.GetFor(locale, "notify.series.archived.body", new
                {
                    series = series.Title,
                    files = !removeFiles ? "kept" : permanent ? "deleted" : "binned",
                }),
                SeriesTitle: series.Title,
                SeriesId: series.Id,
                Url: $"/series/{series.Id}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the archived notification for series {SeriesId}", series.Id);
        }
    }

    /// <summary>
    /// Runs <see cref="RefreshMonitoredSeriesJob.RefreshSeriesAsync"/> in its own scope, the way
    /// <see cref="HealthSourceRecovery"/> does: chapters released while the series was archived are
    /// then queued, bulk-held and announced like a scheduled pass, where a bare sync would insert
    /// them as known and never queue them.
    /// </summary>
    private Task StartRefresh(int seriesId)
    {
        var stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
        return Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var refresh = ActivatorUtilities.CreateInstance<RefreshMonitoredSeriesJob>(scope.ServiceProvider);
                await refresh.RefreshSeriesAsync(seriesId, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh after restoring series {SeriesId} failed", seriesId);
            }
        }, CancellationToken.None);
    }
}
