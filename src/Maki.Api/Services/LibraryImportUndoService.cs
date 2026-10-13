using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Paths;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="FileCount">Files the import registered.</param>
/// <param name="BuiltCount">CBZs Maki built from other formats, which undo removes.</param>
/// <param name="LinkPending">The series is still waiting on its source match or file link.</param>
/// <param name="SeriesGone">The series was deleted since, so there is nothing left to undo.</param>
/// <param name="DiskPending">Undone, but part of the disk half failed; undoing again retries it.</param>
public record ImportBatchFolderDto(
    int Id,
    string OriginalFolderName,
    string FolderName,
    int? SeriesId,
    string SeriesTitle,
    bool CreatedSeries,
    string FolderAction,
    int FileCount,
    int BuiltCount,
    DateTime? UndoneAt,
    bool LinkPending,
    bool SeriesGone,
    bool DiskPending);

public record ImportBatchDto(string BatchId, int RootFolderId, DateTime CreatedAt, IReadOnlyList<ImportBatchFolderDto> Folders);

/// <param name="Error">Localized reason the folder was not undone.</param>
/// <param name="Warnings">Localized notes on a folder that was undone but not completely put back.</param>
public record ImportUndoOutcome(
    int Id, string FolderName, bool Undone, string? Error, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Undoes a library import folder by folder, from what <see cref="ImportBatchFolder"/> recorded:
/// the registered rows go, a series the import created goes, CBZs Maki built are removed only while
/// the original they came from is still there, and the folder moves back to its old name. A file the
/// user had before the import is never deleted.
/// <para>
/// A series the import created is removed only when nothing was downloaded into it and nobody read
/// it since; otherwise the undo is refused and says why. Its deferred source match and file link are
/// cancelled first, under the same flags the worker reads, so the undo never races the link stage.
/// </para>
/// </summary>
public class LibraryImportUndoService(
    MakiDbContext db,
    SourceMatchQueue matchQueue,
    CoverService coverService,
    StatsEventService stats,
    ReaderArchiveCache archives,
    MangaBakaLocalStore mangaBakaStore,
    ILocalizer localizer,
    ICurrentUser currentUser,
    ILogger<LibraryImportUndoService> logger)
{
    /// <summary>How long an import stays undoable; <c>HousekeepingJob</c> prunes older records.</summary>
    public const int RetentionDays = 30;

    /// <summary>How long an undo waits for a source match already in flight before giving up.</summary>
    internal static TimeSpan MatchWait { get; set; } = TimeSpan.FromSeconds(30);

    private const int MaxBatches = 20;

    public async Task<List<ImportBatchDto>> RecentAsync(int? rootFolderId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-RetentionDays);
        var rows = (await db.ImportBatchFolders
                .AsNoTracking()
                .Where(r => r.CreatedAt >= since && (rootFolderId == null || r.RootFolderId == rootFolderId))
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .ToListAsync(ct))
            .Where(r => CanSeeRoot(r.RootFolderId))
            .ToList();

        var seriesIds = rows.Select(r => r.SeriesId).OfType<int>().Distinct().ToList();
        var series = await db.Series
            .IgnoreQueryFilters()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, Pending = s.SourceMatchPending || s.PendingImportLink != PendingImportLink.None })
            .ToDictionaryAsync(s => s.Id, s => s.Pending, ct);

        return rows
            .GroupBy(r => r.BatchId)
            .Take(MaxBatches)
            .Select(g => new ImportBatchDto(
                g.Key,
                g.First().RootFolderId,
                g.Min(r => r.CreatedAt),
                g.OrderBy(r => r.OriginalFolderName, StringComparer.OrdinalIgnoreCase)
                    .Select(r =>
                    {
                        var operations = ImportOperations.Parse(r.OperationsJson);
                        var exists = r.SeriesId is { } id && series.ContainsKey(id);
                        return new ImportBatchFolderDto(
                            r.Id, r.OriginalFolderName, r.FolderName, r.SeriesId, r.SeriesTitle, r.CreatedSeries,
                            operations.FolderAction, operations.RegisteredFileIds.Count, operations.Built.Count,
                            r.UndoneAt, exists && series[r.SeriesId!.Value], !exists && r.UndoneAt is null,
                            r.UndoneAt is not null && operations.DiskPending);
                    })
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// Every folder of the batch not undone yet, plus any whose disk half is still pending, or null
    /// when the caller can see none of it. One folder failing does not stop the rest. A folder whose
    /// series was removed since has nothing to undo and is left out, as the list shows it.
    /// </summary>
    public async Task<List<ImportUndoOutcome>?> UndoBatchAsync(string batchId, CancellationToken ct)
    {
        var rows = (await db.ImportBatchFolders
                .AsNoTracking()
                .Where(r => r.BatchId == batchId)
                .Select(r => new { r.Id, r.RootFolderId, r.SeriesId, r.UndoneAt, r.OriginalFolderName, r.OperationsJson })
                .ToListAsync(ct))
            .Where(r => CanSeeRoot(r.RootFolderId))
            .ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var seriesIds = rows.Select(r => r.SeriesId).OfType<int>().Distinct().ToList();
        var existing = (await db.Series.IgnoreQueryFilters()
                .Where(s => seriesIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var outcomes = new List<ImportUndoOutcome>();
        foreach (var row in rows.Where(r =>
                     r.UndoneAt is null
                         ? r.SeriesId is { } id && existing.Contains(id)
                         : ImportOperations.Parse(r.OperationsJson).DiskPending))
        {
            try
            {
                outcomes.Add(await UndoFolderAsync(row.Id, ct)
                    ?? new ImportUndoOutcome(row.Id, row.OriginalFolderName, false,
                        localizer.Get("error.libraryImport.undoFailed")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Undoing the import of '{Folder}' failed", row.OriginalFolderName);
                db.ChangeTracker.Clear();
                outcomes.Add(new ImportUndoOutcome(row.Id, row.OriginalFolderName, false,
                    localizer.Get("error.libraryImport.undoFailed")));
            }
        }

        return outcomes;
    }

    /// <summary>Null when there is no such record the caller can see.</summary>
    public async Task<ImportUndoOutcome?> UndoFolderAsync(int id, CancellationToken ct)
    {
        var row = await db.ImportBatchFolders.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null || !CanSeeRoot(row.RootFolderId))
        {
            return null;
        }

        try
        {
            return await UndoAsync(row, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Undoing the import of '{Folder}' failed", row.OriginalFolderName);
            // Changes a failed save left tracked would otherwise go out with the next folder's save.
            db.ChangeTracker.Clear();
            return Refused(row, "error.libraryImport.undoFailed");
        }
    }

    private async Task<ImportUndoOutcome> UndoAsync(ImportBatchFolder row, CancellationToken ct)
    {
        var operations = ImportOperations.Parse(row.OperationsJson);
        if (row.UndoneAt is not null)
        {
            return operations.DiskPending
                ? await RetryDiskAsync(row, operations, ct)
                : Refused(row, "error.libraryImport.undoAlreadyDone");
        }

        var root = await db.RootFolders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == row.RootFolderId, ct);
        if (root is null || !Directory.Exists(root.Path))
        {
            return Refused(row, "error.libraryImport.rootUnavailable");
        }

        var series = row.SeriesId is { } seriesId
            ? await db.Series.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, ct)
            : null;
        if (series is null)
        {
            return Refused(row, "error.libraryImport.undoSeriesGone");
        }

        // Removing a series is what DeleteSeries guards; the person who ran the import may take back
        // what they added without it.
        if (row.CreatedSeries && !currentUser.Has(MakiPermission.DeleteSeries) &&
            (row.UserId is null || row.UserId != currentUser.UserId))
        {
            return Refused(row, "error.libraryImport.undoNeedsDeleteSeries");
        }

        var registered = operations.RegisteredFileIds.ToHashSet();
        if (await RefusalAsync(row, series.Id, registered, ct) is { } early)
        {
            return Refused(row, early);
        }

        // The deferred stages first. Clearing the flags the worker reads means a match or link
        // still queued finds nothing to do; a match already running is waited out, since it holds
        // no lock an undo could take to stop it.
        // Replaced by what the clear below actually cleared, read here only in case it throws first.
        var before = (series.SourceMatchPending, series.PendingImportLink);
        var matchMayHaveRun = false;
        var committed = false;
        try
        {
            before = await StopPendingAsync(series.Id, row.CreatedSeries, ct);
            // A run registered after the clear reads the cleared flag and matches nothing. One that
            // was already registered may have read the flag first and done the match itself.
            matchMayHaveRun = !matchQueue.MatchInProgress(series.Id).IsCompleted;
            try
            {
                await matchQueue.MatchInProgress(series.Id).WaitAsync(MatchWait, ct);
            }
            catch (TimeoutException)
            {
                await RestorePendingAsync(series.Id, before, matchRunning: true);
                return Refused(row, "error.libraryImport.undoMatchRunning");
            }

            // The link stage links under this lock, so once it is held no link is half done.
            using var seriesLock = await SeriesLocks.SeriesAsync(series.Id, ct);

            // Another undo of this folder may have finished while this one waited for the lock.
            if (await db.ImportBatchFolders.AsNoTracking().Where(r => r.Id == row.Id)
                    .Select(r => r.UndoneAt).FirstOrDefaultAsync(ct) is not null)
            {
                return Refused(row, "error.libraryImport.undoAlreadyDone");
            }

            if (!await db.Series.IgnoreQueryFilters().AnyAsync(s => s.Id == series.Id, ct))
            {
                return Refused(row, "error.libraryImport.undoSeriesGone");
            }

            if (await RefusalAsync(row, series.Id, registered, ct) is { } late)
            {
                await RestorePendingAsync(series.Id, before, matchRunning: false, matchMayHaveRun);
                return Refused(row, late);
            }

            using var folderNameLock = await SeriesLocks.FolderNamesAsync(ct);
            var targetDir = LibraryPaths.ResolveNoLinks(root.Path, row.FolderName);
            var sourceDir = LibraryPaths.ResolveNoLinks(root.Path, row.OriginalFolderName);
            if (await FolderProblemAsync(row, operations, series.Id, registered, targetDir, sourceDir, ct) is { } problem)
            {
                await RestorePendingAsync(series.Id, before, matchRunning: false, matchMayHaveRun);
                return Refused(row, problem, new { name = row.OriginalFolderName, folder = row.FolderName });
            }

            // The database first: if the disk half then fails part way, nothing points at a file that
            // is gone, and every original is still on disk.
            var tracked = await db.Series.IgnoreQueryFilters().FirstAsync(s => s.Id == series.Id, ct);
            var linked = await db.Chapters
                .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null &&
                            registered.Contains(c.ChapterFileId.Value))
                .ToListAsync(ct);
            foreach (var chapter in linked)
            {
                chapter.ChapterFileId = null;
                if (operations.PreviousFileRemovedAt.TryGetValue(chapter.Id, out var removedAt))
                {
                    chapter.FileRemovedAt = removedAt;
                }
            }

            var files = await db.ChapterFiles
                .Where(f => f.SeriesId == series.Id && registered.Contains(f.Id))
                .ToListAsync(ct);
            db.ChapterFiles.RemoveRange(files);

            var seriesKey = SeriesIdentity.For(tracked);
            string? removedPayload = null;
            if (row.CreatedSeries)
            {
                // The same record SeriesController.Delete leaves of a removed series.
                removedPayload = await SeriesRemovalRecord.PayloadAsync(tracked, mangaBakaStore, logger, ct);
                await SeriesRemovalRecord.BumpRecommendationOwnersAsync(db, series.Id, ct);
                db.Series.Remove(tracked);
            }
            else
            {
                tracked.PendingImportLink = PendingImportLink.None;
                if (operations.PreviousSeriesFolderName is { } previousFolder)
                {
                    tracked.FolderName = previousFolder;
                }

                if (operations.PreviousRootFolderId is { } previousRoot)
                {
                    tracked.RootFolderId = previousRoot;
                }
            }

            // A metadata refresh may have rewritten the cover since; its hash goes before the record does.
            if (operations.WroteCover && coverService.LibraryCoverHash(series.Id) is { } coverHash &&
                !operations.CoverHashes.Contains(coverHash, StringComparer.OrdinalIgnoreCase))
            {
                operations.CoverHashes.Add(coverHash);
            }

            row.UndoneAt = DateTime.UtcNow;
            operations.DiskPending = true;
            row.OperationsJson = operations.Serialize();
            await db.SaveChangesAsync(CancellationToken.None);
            committed = true;

            foreach (var file in files)
            {
                archives.Invalidate(file.Id);
            }

            if (row.CreatedSeries)
            {
                coverService.DeleteCover(series.Id);
                await stats.RecordAsync(StatsEventType.SeriesRemoved, null, series.Title,
                    payloadJson: removedPayload, seriesKey: seriesKey, ct: CancellationToken.None);
            }

            var warnings = new List<string>();
            var complete = RestoreDisk(row, operations, root.Path, targetDir!, sourceDir!, warnings);
            await SaveDiskOutcomeAsync(row, operations, complete, warnings);
            logger.LogInformation("Undid the import of '{Folder}' into series {SeriesId}", row.OriginalFolderName,
                series.Id);
            return new ImportUndoOutcome(row.Id, row.OriginalFolderName, true, null,
                warnings.Count > 0 ? warnings : null);
        }
        catch (Exception ex) when (!committed)
        {
            // Nothing was committed, so the series is as it was: give it back what the import owes it.
            logger.LogWarning(ex, "Undoing the import of '{Folder}' failed before anything changed", row.OriginalFolderName);
            db.ChangeTracker.Clear();
            try
            {
                await RestorePendingAsync(series.Id, before, matchRunning: false, matchMayHaveRun);
            }
            catch (Exception restoreEx)
            {
                logger.LogError(restoreEx, "Could not restore the pending match and link of series {SeriesId}", series.Id);
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            return Refused(row, "error.libraryImport.undoFailed");
        }
    }

    /// <summary>
    /// The disk half of an undo whose database half is done, again: a folder that could not be moved
    /// back the first time (a file locked by another program, a share that dropped) can be once the
    /// cause is gone.
    /// </summary>
    private async Task<ImportUndoOutcome> RetryDiskAsync(
        ImportBatchFolder row, ImportOperations operations, CancellationToken ct)
    {
        var root = await db.RootFolders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == row.RootFolderId, ct);
        if (root is null || !Directory.Exists(root.Path))
        {
            return Refused(row, "error.libraryImport.rootUnavailable");
        }

        using var folderNameLock = await SeriesLocks.FolderNamesAsync(ct);
        var targetDir = LibraryPaths.ResolveNoLinks(root.Path, row.FolderName);
        var sourceDir = LibraryPaths.ResolveNoLinks(root.Path, row.OriginalFolderName);
        if (targetDir is null || sourceDir is null)
        {
            return Refused(row, "error.libraryImport.undoFolderGone", new { folder = row.FolderName });
        }

        var warnings = new List<string>();
        var complete = RestoreDisk(row, operations, root.Path, targetDir, sourceDir, warnings);
        await SaveDiskOutcomeAsync(row, operations, complete, warnings);
        return new ImportUndoOutcome(row.Id, row.OriginalFolderName, true, null, warnings.Count > 0 ? warnings : null);
    }

    private async Task SaveDiskOutcomeAsync(
        ImportBatchFolder row, ImportOperations operations, bool complete, List<string> warnings)
    {
        operations.DiskPending = !complete;
        row.OperationsJson = operations.Serialize();
        await db.SaveChangesAsync(CancellationToken.None);
        if (!complete)
        {
            warnings.Add(localizer.Get("error.libraryImport.undoDiskPending", new { folder = row.FolderName }));
        }
    }

    /// <summary>
    /// Why the undo cannot put the folder back as it was, or null: the folder is gone, its old name
    /// is taken on disk or by another series (a keep-new-standard series need not have its folder on
    /// disk yet), or the series' files were renamed since, so the recorded names are stale.
    /// </summary>
    private async Task<string?> FolderProblemAsync(
        ImportBatchFolder row, ImportOperations operations, int seriesId, IReadOnlySet<int> registered,
        string? targetDir, string? sourceDir, CancellationToken ct)
    {
        if (targetDir is null || sourceDir is null || !Directory.Exists(targetDir))
        {
            return "error.libraryImport.undoFolderGone";
        }

        var moves = operations.FolderAction is ImportFolderAction.Rename or ImportFolderAction.Merge;
        if (operations.FolderAction == ImportFolderAction.Rename &&
            !LibraryPaths.IsSameDirectory(sourceDir, targetDir) && Directory.Exists(sourceDir))
        {
            return "error.libraryImport.undoFolderTaken";
        }

        if (moves && (await SeriesCreationService.SeriesFoldersInRootAsync(db, row.RootFolderId, seriesId, ct))
                .Contains(row.OriginalFolderName))
        {
            return "error.libraryImport.folderOwnedByOtherSeries";
        }

        var current = await db.ChapterFiles
            .Where(f => registered.Contains(f.Id))
            .Select(f => new { f.Id, f.RelativePath })
            .ToListAsync(ct);
        var renamed = current.Any(f =>
            operations.RegisteredPaths.TryGetValue(f.Id, out var recorded) &&
            !string.Equals(LibraryPaths.ComparisonKey(recorded), LibraryPaths.ComparisonKey(f.RelativePath),
                StringComparison.OrdinalIgnoreCase));
        return renamed ? "error.libraryImport.undoFilesRenamed" : null;
    }

    /// <summary>
    /// Why the folder cannot be undone right now, or null. A download since the import would leave
    /// the series with files undo did not register, or move them with the folder; a reader's progress
    /// is history that removing the series would cascade away.
    /// </summary>
    private async Task<string?> RefusalAsync(
        ImportBatchFolder row, int seriesId, IReadOnlySet<int> registered, CancellationToken ct)
    {
        if (await SeriesLocks.InFlight(db.DownloadQueue).AnyAsync(q => q.SeriesId == seriesId, ct))
        {
            return "error.libraryImport.undoDownloading";
        }

        var fileIds = await db.ChapterFiles.Where(f => f.SeriesId == seriesId).Select(f => f.Id).ToListAsync(ct);
        if (fileIds.Any(id => !registered.Contains(id)))
        {
            return "error.libraryImport.undoDownloadedSince";
        }

        if (row.CreatedSeries &&
            await db.ChapterProgress.IgnoreQueryFilters().AnyAsync(p => p.SeriesId == seriesId, ct))
        {
            return "error.libraryImport.undoHasProgress";
        }

        return null;
    }

    /// <summary>
    /// Cancels what the import still owes the series. For a series the import created that is the
    /// source match as well as the link; a series already in the library keeps any match it was
    /// waiting on before the import.
    /// <para>
    /// Each flag is tested and cleared in one update, so what comes back is exactly what was
    /// cleared, even when a re-match was flagged after the undo first read the series.
    /// </para>
    /// </summary>
    private async Task<(bool MatchPending, PendingImportLink Link)> StopPendingAsync(
        int seriesId, bool created, CancellationToken ct)
    {
        var query = db.Series.IgnoreQueryFilters().Where(s => s.Id == seriesId);
        var matchPending = created && await query.Where(s => s.SourceMatchPending)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceMatchPending, false), ct) > 0;
        var link = PendingImportLink.None;
        foreach (var owed in new[] { PendingImportLink.LinkAndComicInfo, PendingImportLink.Link })
        {
            if (await query.Where(s => s.PendingImportLink == owed)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.PendingImportLink, PendingImportLink.None), ct) > 0)
            {
                link = owed;
            }
        }

        return (matchPending, link);
    }

    /// <summary>
    /// Puts back what this undo cleared after it was refused, and queues it again. Only what
    /// <paramref name="before"/> says this undo cleared is written: an undo that overlapped another
    /// one cleared nothing, and writing its empty snapshot would wipe the flags the other just put back.
    /// A match that ran while the undo waited is not repeated: the series then only owes its link.
    /// Only a run that was already registered when the flag was cleared can have done it
    /// (<paramref name="matchMayHaveRun"/>), and then the mappings it saved are what tells; otherwise
    /// the flag goes back, so a re-match still queued for a series that has mappings is not lost.
    /// </summary>
    private async Task RestorePendingAsync(
        int seriesId, (bool MatchPending, PendingImportLink Link) before, bool matchRunning,
        bool matchMayHaveRun = false)
    {
        if (before is (false, PendingImportLink.None))
        {
            return;
        }

        var none = CancellationToken.None;
        var query = db.Series.IgnoreQueryFilters().Where(s => s.Id == seriesId);
        // The link marker first, so a match queued below always finds it and routes the link.
        if (before.Link != PendingImportLink.None)
        {
            await query.ExecuteUpdateAsync(u => u.SetProperty(s => s.PendingImportLink, before.Link), none);
        }

        if (matchRunning)
        {
            // The match still running routes the link when it ends, so the flags go back as they
            // were and no link is queued: one queued now would link against no chapters and clear
            // the marker under the match. A run that read the cleared marker before this put it back
            // gets a successor, which waits for it and then routes the link or matches again.
            if (before.MatchPending)
            {
                await query.ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceMatchPending, true), none);
            }

            matchQueue.Enqueue(seriesId, SourceMatchLane.Background);
            return;
        }

        if (before.MatchPending &&
            (!matchMayHaveRun ||
             !await db.SourceMappings.IgnoreQueryFilters().AnyAsync(m => m.SeriesId == seriesId, none)))
        {
            await query.ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceMatchPending, true), none);
            matchQueue.Enqueue(seriesId, SourceMatchLane.Background);
        }

        // Whoever flagged a still-pending match queued it; its end routes the link.
        if (before.Link != PendingImportLink.None &&
            !await query.Select(s => s.SourceMatchPending).FirstOrDefaultAsync(none))
        {
            matchQueue.EnqueueLink(seriesId);
        }
    }

    /// <summary>
    /// Whether the original a built CBZ came from is still whole: re-read, it still holds every page
    /// the build read. Only then is the CBZ a second copy that undo may delete. A record without
    /// pages (a nested archive) can only be matched by its entry, which still has to be there.
    /// </summary>
    internal static bool OriginalIntact(ImportBuiltFile built, string originalPath)
    {
        if (built.Kind is null || built.Pages is null)
        {
            return false;
        }

        if (built.Kind == "looseImages")
        {
            // Read directly rather than through the scanner: once the CBZ sits beside the pages the
            // scanner calls the folder that archive's, not a loose set.
            return built.Pages.Count > 0 && Directory.Exists(originalPath) &&
                   built.Pages.All(page => File.Exists(Path.Combine(originalPath, page)));
        }

        if (!File.Exists(originalPath))
        {
            return false;
        }

        var found = ComicSourceScanner.Scan(originalPath);
        if (built.Entry is not null)
        {
            return found.Any(s => string.Equals(s.Entry, built.Entry, StringComparison.Ordinal));
        }

        return built.Pages.Count > 0 && found.Any(s =>
            s.Entry is null && built.Pages.All(s.Pages.ToHashSet(StringComparer.Ordinal).Contains));
    }

    /// <summary>
    /// The disk half: removes the CBZs Maki built, puts a mislabelled original back under its name,
    /// removes the cover the import wrote, and moves the folder (or a merge's files) back. A built
    /// file whose original is gone or no longer whole is kept, since it is then the only copy, and so
    /// is one the user replaced since.
    /// Returns false when something failed that a retry may fix; the reasons are in
    /// <paramref name="warnings"/>. By now the database no longer refers to any of these files.
    /// <para>
    /// Every step can be repeated: a retry finds the folder under whichever name it has now, a
    /// rebuilt original still set aside is put back even when its CBZ is already gone, and the folder
    /// is not moved while anything inside it still failed, so a retry still finds what is left.
    /// </para>
    /// </summary>
    private bool RestoreDisk(
        ImportBatchFolder row, ImportOperations operations, string rootPath, string targetDir, string sourceDir,
        List<string> warnings)
    {
        var complete = true;
        var movedBack = operations.FolderAction == ImportFolderAction.Rename &&
                        !LibraryPaths.IsSameDirectory(sourceDir, targetDir) &&
                        !Directory.Exists(targetDir) && Directory.Exists(sourceDir);
        var folderName = movedBack ? row.OriginalFolderName : row.FolderName;
        var folderDir = movedBack ? sourceDir : targetDir;

        // What is put back is dropped from the record, so a retry only repeats what is left.
        var remaining = new List<ImportBuiltFile>();
        foreach (var built in operations.Built)
        {
            var target = LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(folderName, built.Name));
            var rebuilt = built.Action == ImportFileAction.RebuildInPlace;
            var original = rebuilt
                ? built.Aside is null ? null : LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(folderName, built.Aside))
                : LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(folderName, built.Source));
            if (target is null)
            {
                continue;
            }

            if (!File.Exists(target))
            {
                // The CBZ went but the original never came back from where it was set aside.
                if (rebuilt && original is not null && File.Exists(original) && !TryMove(original, target, built))
                {
                    remaining.Add(built);
                    complete = false;
                }

                continue;
            }

            if (original is null || ComicSourceConverter.IsSameFile(target, original) || !OriginalIntact(built, original))
            {
                warnings.Add(localizer.Get("error.libraryImport.undoKeptBuilt", new { name = built.Name }));
                remaining.Add(built);
                continue;
            }

            if (built.Digest is not null &&
                !string.Equals(built.Digest, ImportBuiltFile.DigestOf(target), StringComparison.OrdinalIgnoreCase))
            {
                // Not the file Maki built any more: the user put another one under its name.
                warnings.Add(localizer.Get("error.libraryImport.undoKeptReplaced", new { name = built.Name }));
                continue;
            }

            try
            {
                if (rebuilt)
                {
                    // One rename over the CBZ, so there is no moment with neither file under the name.
                    File.Move(original, target, overwrite: true);
                }
                else
                {
                    File.Delete(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {File}, which the undone import built", target);
                warnings.Add(localizer.Get("error.libraryImport.undoKeptBuilt", new { name = built.Name }));
                remaining.Add(built);
                complete = false;
            }
        }

        operations.Built = remaining;

        if (operations.WroteCover)
        {
            var cover = Path.Combine(folderDir, LibraryImportService.LibraryCoverFileName);
            try
            {
                if (File.Exists(cover))
                {
                    if (operations.CoverHashes.Contains(CoverService.Sha256Of(cover), StringComparer.OrdinalIgnoreCase))
                    {
                        File.Delete(cover);
                    }
                    else
                    {
                        warnings.Add(localizer.Get("error.libraryImport.undoKeptCover"));
                    }
                }

                operations.WroteCover = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {File}, which the undone import wrote", cover);
                complete = false;
            }
        }

        var folderNotMoved = localizer.Get("error.libraryImport.undoFolderNotMoved",
            new { name = row.OriginalFolderName, folder = row.FolderName });
        if (!complete)
        {
            // Moving the folder now would leave what failed under a name a retry no longer looks in.
            if (operations.FolderAction is ImportFolderAction.Rename or ImportFolderAction.Merge && !movedBack)
            {
                warnings.Add(folderNotMoved);
            }

            return false;
        }

        try
        {
            if (operations.FolderAction == ImportFolderAction.Rename && !movedBack && Directory.Exists(targetDir))
            {
                if (LibraryPaths.IsSameDirectory(sourceDir, targetDir) || !Directory.Exists(sourceDir))
                {
                    SeriesRenameService.MovePath(targetDir, sourceDir, Directory.Move);
                }
                else
                {
                    // Something took the old name since; the folder stays where it is until it is free.
                    warnings.Add(folderNotMoved);
                    complete = false;
                }
            }
            else if (operations.FolderAction == ImportFolderAction.Merge)
            {
                foreach (var moved in operations.Moved)
                {
                    var from = LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(row.FolderName, moved));
                    var to = LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(row.OriginalFolderName, moved));
                    if (to is not null && File.Exists(to) && (from is null || !File.Exists(from)))
                    {
                        continue;
                    }

                    if (from is null || to is null || !File.Exists(from) || File.Exists(to))
                    {
                        warnings.Add(localizer.Get("error.libraryImport.undoFileNotMoved",
                            new { name = moved, folder = row.FolderName }));
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not move '{Folder}' back to '{Original}'", row.FolderName, row.OriginalFolderName);
            warnings.Add(folderNotMoved);
            complete = false;
        }

        return complete;

        bool TryMove(string from, string to, ImportBuiltFile built)
        {
            try
            {
                File.Move(from, to);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not put {File} back under its own name", from);
                warnings.Add(localizer.Get("error.libraryImport.undoKeptBuilt", new { name = built.Name }));
                return false;
            }
        }
    }

    private ImportUndoOutcome Refused(ImportBatchFolder row, string key, object? args = null) =>
        new(row.Id, row.OriginalFolderName, false, localizer.Get(key, args));

    private bool CanSeeRoot(int rootFolderId) =>
        currentUser.AllRootFolders || currentUser.RootFolderIds.Contains(rootFolderId);
}
