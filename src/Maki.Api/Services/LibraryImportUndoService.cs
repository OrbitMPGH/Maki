using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Paths;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="FileCount">Files the import registered.</param>
/// <param name="BuiltCount">CBZs Maki built from other formats, which undo removes.</param>
/// <param name="LinkPending">The series is still waiting on its source match or file link.</param>
/// <param name="SeriesGone">The series was deleted since, so there is nothing left to undo.</param>
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
    bool SeriesGone);

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
                            r.UndoneAt, exists && series[r.SeriesId!.Value], !exists);
                    })
                    .ToList()))
            .ToList();
    }

    /// <summary>Every folder of the batch not undone yet, or null when the caller can see none of it.</summary>
    public async Task<List<ImportUndoOutcome>?> UndoBatchAsync(string batchId, CancellationToken ct)
    {
        var ids = (await db.ImportBatchFolders
                .AsNoTracking()
                .Where(r => r.BatchId == batchId)
                .Select(r => new { r.Id, r.RootFolderId, r.UndoneAt })
                .ToListAsync(ct))
            .Where(r => CanSeeRoot(r.RootFolderId))
            .ToList();
        if (ids.Count == 0)
        {
            return null;
        }

        var outcomes = new List<ImportUndoOutcome>();
        foreach (var id in ids.Where(r => r.UndoneAt is null).Select(r => r.Id))
        {
            outcomes.Add((await UndoFolderAsync(id, ct))!);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Undoing the import of '{Folder}' failed", row.OriginalFolderName);
            return Refused(row, "error.libraryImport.undoFailed");
        }
    }

    private async Task<ImportUndoOutcome> UndoAsync(ImportBatchFolder row, CancellationToken ct)
    {
        if (row.UndoneAt is not null)
        {
            return Refused(row, "error.libraryImport.undoAlreadyDone");
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

        var operations = ImportOperations.Parse(row.OperationsJson);
        var registered = operations.RegisteredFileIds.ToHashSet();
        if (await RefusalAsync(row, series.Id, registered, ct) is { } early)
        {
            return Refused(row, early);
        }

        // The deferred stages first. Clearing the flags the worker reads means a match or link
        // still queued finds nothing to do; a match already running is waited out, since it holds
        // no lock an undo could take to stop it.
        var before = (series.SourceMatchPending, series.PendingImportLink);
        await StopPendingAsync(series.Id, row.CreatedSeries, ct);
        try
        {
            await matchQueue.MatchInProgress(series.Id).WaitAsync(MatchWait, ct);
        }
        catch (TimeoutException)
        {
            await RestorePendingAsync(series.Id, row.CreatedSeries, before);
            return Refused(row, "error.libraryImport.undoMatchRunning");
        }

        // The link stage links under this lock, so once it is held no link is half done.
        using var seriesLock = await SeriesLocks.SeriesAsync(series.Id, ct);
        if (await RefusalAsync(row, series.Id, registered, ct) is { } late)
        {
            await RestorePendingAsync(series.Id, row.CreatedSeries, before);
            return Refused(row, late);
        }

        using var folderNameLock = await SeriesLocks.FolderNamesAsync(ct);
        var targetDir = LibraryPaths.ResolveNoLinks(root.Path, row.FolderName);
        var sourceDir = LibraryPaths.ResolveNoLinks(root.Path, row.OriginalFolderName);
        var folderProblem = targetDir is null || sourceDir is null || !Directory.Exists(targetDir)
            ? "error.libraryImport.undoFolderGone"
            : operations.FolderAction == ImportFolderAction.Rename &&
              !LibraryPaths.IsSameDirectory(sourceDir, targetDir) && Directory.Exists(sourceDir)
                ? "error.libraryImport.undoFolderTaken"
                : null;
        if (folderProblem is not null)
        {
            await RestorePendingAsync(series.Id, row.CreatedSeries, before);
            return Refused(row, folderProblem, new { name = row.OriginalFolderName, folder = row.FolderName });
        }

        // The database first: if the disk half then fails part way, nothing points at a file that
        // is gone, and every original is still on disk.
        var tracked = await db.Series.IgnoreQueryFilters().FirstAsync(s => s.Id == series.Id, ct);
        var linked = await db.Chapters
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null && registered.Contains(c.ChapterFileId.Value))
            .ToListAsync(ct);
        foreach (var chapter in linked)
        {
            chapter.ChapterFileId = null;
        }

        var files = await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id && registered.Contains(f.Id))
            .ToListAsync(ct);
        db.ChapterFiles.RemoveRange(files);

        var seriesKey = SeriesIdentity.For(tracked);
        if (row.CreatedSeries)
        {
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

        row.UndoneAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        foreach (var file in files)
        {
            archives.Invalidate(file.Id);
        }

        if (row.CreatedSeries)
        {
            coverService.DeleteCover(series.Id);
            await stats.RecordAsync(StatsEventType.SeriesRemoved, null, series.Title, seriesKey: seriesKey,
                ct: CancellationToken.None);
        }

        var warnings = new List<string>();
        RestoreDisk(row, operations, root.Path, targetDir!, sourceDir!, warnings);
        logger.LogInformation("Undid the import of '{Folder}' into series {SeriesId}", row.OriginalFolderName, series.Id);
        return new ImportUndoOutcome(row.Id, row.OriginalFolderName, true, null, warnings.Count > 0 ? warnings : null);
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
    /// </summary>
    private Task StopPendingAsync(int seriesId, bool created, CancellationToken ct) =>
        created
            ? db.Series.IgnoreQueryFilters().Where(s => s.Id == seriesId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.SourceMatchPending, false)
                .SetProperty(s => s.PendingImportLink, PendingImportLink.None), ct)
            : db.Series.IgnoreQueryFilters().Where(s => s.Id == seriesId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.PendingImportLink, PendingImportLink.None), ct);

    /// <summary>
    /// Puts the deferred stages back after a refused undo, and queues them again. A match that ran
    /// while the undo waited is not repeated: the series then only owes its link.
    /// </summary>
    private async Task RestorePendingAsync(
        int seriesId, bool created, (bool MatchPending, PendingImportLink Link) before)
    {
        var none = CancellationToken.None;
        var query = db.Series.IgnoreQueryFilters().Where(s => s.Id == seriesId);
        bool matchPending;
        if (created)
        {
            matchPending = before.MatchPending &&
                           !await db.SourceMappings.IgnoreQueryFilters().AnyAsync(m => m.SeriesId == seriesId, none);
            await query.ExecuteUpdateAsync(u => u
                .SetProperty(s => s.SourceMatchPending, matchPending)
                .SetProperty(s => s.PendingImportLink, before.Link), none);
            if (matchPending)
            {
                matchQueue.Enqueue(seriesId, SourceMatchLane.Background);
            }
        }
        else
        {
            await query.ExecuteUpdateAsync(u => u.SetProperty(s => s.PendingImportLink, before.Link), none);
            // Whoever flagged a still-pending match queued it; its end routes the link.
            matchPending = await query.Select(s => s.SourceMatchPending).FirstOrDefaultAsync(none);
        }

        if (!matchPending && before.Link != PendingImportLink.None)
        {
            matchQueue.EnqueueLink(seriesId);
        }
    }

    /// <summary>
    /// The disk half: removes the CBZs Maki built, puts a mislabelled original back under its name,
    /// removes the cover the import wrote, and moves the folder (or a merge's files) back. A built
    /// file whose original is gone is kept, since it is then the only copy. Failures become warnings:
    /// by now the database no longer refers to any of these files.
    /// </summary>
    private void RestoreDisk(
        ImportBatchFolder row, ImportOperations operations, string rootPath, string targetDir, string sourceDir,
        List<string> warnings)
    {
        foreach (var built in operations.Built)
        {
            var target = LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(row.FolderName, built.Name));
            var source = LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(row.FolderName, built.Source));
            if (target is null || source is null || !File.Exists(target))
            {
                continue;
            }

            try
            {
                if (built.Action == ImportFileAction.RebuildInPlace)
                {
                    var aside = built.Aside is null
                        ? null
                        : LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(row.FolderName, built.Aside));
                    if (aside is null || !File.Exists(aside))
                    {
                        warnings.Add(localizer.Get("error.libraryImport.undoKeptBuilt", new { name = built.Name }));
                        continue;
                    }

                    File.Delete(target);
                    File.Move(aside, target);
                }
                else if (!ComicSourceConverter.IsSameFile(target, source) &&
                         (File.Exists(source) || Directory.Exists(source)))
                {
                    File.Delete(target);
                }
                else
                {
                    warnings.Add(localizer.Get("error.libraryImport.undoKeptBuilt", new { name = built.Name }));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {File}, which the undone import built", target);
                warnings.Add(localizer.Get("error.libraryImport.undoKeptBuilt", new { name = built.Name }));
            }
        }

        if (operations.WroteCover)
        {
            var cover = Path.Combine(targetDir, LibraryImportService.LibraryCoverFileName);
            try
            {
                if (File.Exists(cover))
                {
                    File.Delete(cover);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {File}, which the undone import wrote", cover);
            }
        }

        try
        {
            if (operations.FolderAction == ImportFolderAction.Rename)
            {
                SeriesRenameService.MovePath(targetDir, sourceDir, Directory.Move);
            }
            else if (operations.FolderAction == ImportFolderAction.Merge)
            {
                foreach (var moved in operations.Moved)
                {
                    var from = LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(row.FolderName, moved));
                    var to = LibraryPaths.ResolveNoLinks(rootPath, Path.Combine(row.OriginalFolderName, moved));
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
            warnings.Add(localizer.Get("error.libraryImport.undoFolderNotMoved",
                new { name = row.OriginalFolderName, folder = row.FolderName }));
        }
    }

    private ImportUndoOutcome Refused(ImportBatchFolder row, string key, object? args = null) =>
        new(row.Id, row.OriginalFolderName, false, localizer.Get(key, args));

    private bool CanSeeRoot(int rootFolderId) =>
        currentUser.AllRootFolders || currentUser.RootFolderIds.Contains(rootFolderId);
}
