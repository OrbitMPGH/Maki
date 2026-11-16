using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum SeriesMoveFailure
{
    None,
    NotFound,
    BadRequest,
    Conflict,
    ServerError,
}

/// <summary>What a series move did, or why it refused. <c>ErrorKey</c> and <c>Args</c> name a server catalogue message.</summary>
public sealed record SeriesMoveResult(
    SeriesMoveFailure Failure,
    string? ErrorKey = null,
    object? Args = null,
    Series? Series = null,
    string? FromPath = null,
    string? ToPath = null)
{
    public static readonly SeriesMoveResult NotFound = new(SeriesMoveFailure.NotFound);

    public bool Succeeded => Failure == SeriesMoveFailure.None;

    public static SeriesMoveResult Fail(string key, object? args = null) => new(SeriesMoveFailure.BadRequest, key, args);

    public static SeriesMoveResult Conflict(string key, object? args = null) => new(SeriesMoveFailure.Conflict, key, args);

    public static SeriesMoveResult ServerError(string key, object? args = null) => new(SeriesMoveFailure.ServerError, key, args);
}

/// <summary>
/// Relocates a series to a different root folder: repoints <see cref="Series.RootFolderId"/> and,
/// when <c>moveFiles</c> is true, moves the on-disk folder too (same <see cref="Series.FolderName"/>,
/// so every <see cref="ChapterFile.RelativePath"/> stays valid unchanged). Either way, re-triggers a
/// Kavita scan of both the old location (so Kavita notices the files are gone) and the new one (so
/// it picks them back up). A file move is refused while a download for this series is in flight,
/// since it writes into the old folder mid-move otherwise; a DB-only repoint isn't, since nothing on
/// disk is touched.
/// <para>
/// Shared by <c>POST series/{id}/move</c> and the queued bulk move, so both refuse and roll back the
/// same way. The destination must be one of the caller's root folders (<see cref="ICurrentUser"/>).
/// </para>
/// </summary>
public class SeriesMoveService(MakiDbContext db, ICurrentUser currentUser, KavitaScanService kavitaScans, ILogger logger)
{
    public async Task<SeriesMoveResult> MoveAsync(int id, int rootFolderId, bool moveFiles, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return SeriesMoveResult.NotFound;
        }

        if (series.RootFolder is null)
        {
            return SeriesMoveResult.Fail("error.series.noRootFolder");
        }

        if (rootFolderId == series.RootFolderId)
        {
            return SeriesMoveResult.Fail("error.series.alreadyInRootFolder");
        }

        using var seriesLock = await SeriesLocks.SeriesAsync(id, ct);

        var destination = await db.RootFolders.FindAsync([rootFolderId], ct);
        if (destination is null)
        {
            return SeriesMoveResult.Fail("error.series.rootFolderNotFound");
        }

        if (!currentUser.AllRootFolders && !currentUser.RootFolderIds.Contains(rootFolderId))
        {
            return SeriesMoveResult.Fail("error.series.rootFolderNotFound");
        }

        // Every folder the series has files in moves with it, or the stored paths of a
        // keep-new-standard import's original folder would point into the old root.
        var folders = await SeriesFolders.ForAsync(db, series, ct);
        var newFolder = Path.Combine(destination.Path, series.FolderName);

        // Add and import never give two series of one root the same folder, so a move must not
        // either. Checked again under the folder-name lock just before the save, once the files
        // have moved and a racing add could have claimed the name.
        if ((await SeriesCreationService.SeriesFoldersInRootAsync(db, destination.Id, id, ct))
            .Contains(series.FolderName))
        {
            return SeriesMoveResult.Conflict("error.series.destinationExists", new { folder = newFolder });
        }

        // A failure part way through puts back whatever already moved, since RootFolderId is
        // not updated and would otherwise resolve it under the old root. The series' own folder
        // moves whole; a folder it only has some files in gives up just those files, the same
        // line Delete draws, so anything else living there stays put.
        var undo = new List<(string From, string To, bool IsDirectory)>();
        var emptiedFolders = new List<string>();
        // Directories the partial-folder branch below created for a single file's destination,
        // deepest first. A failed later file rolls the moved files back but leaves these behind;
        // left in place, the next attempt's Directory.Exists(target) pre-check sees a directory
        // that already exists and fails with error.series.destinationExists.
        var createdDirs = new List<string>();

        void CreateDirectoryTracked(string path)
        {
            var dir = path;
            while (!Directory.Exists(dir))
            {
                createdDirs.Add(dir);
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir)
                {
                    break;
                }

                dir = parent;
            }

            Directory.CreateDirectory(path);
        }

        void RollbackMoves()
        {
            for (var i = undo.Count - 1; i >= 0; i--)
            {
                var (from, to, isDirectory) = undo[i];
                try
                {
                    if (isDirectory)
                    {
                        MoveDirectory(to, from);
                    }
                    else
                    {
                        System.IO.File.Move(to, from);
                    }
                }
                catch (Exception rollbackEx)
                {
                    logger.LogError(rollbackEx, "Could not move {Path} back to {Root} after a failed series move",
                        from, series.RootFolder.Path);
                }
            }

            foreach (var dir in createdDirs)
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }
                catch (Exception rollbackEx)
                {
                    logger.LogError(rollbackEx, "Could not remove {Path} after a failed series move", dir);
                }
            }
        }

        if (moveFiles)
        {
            if (await HasActiveDownloadAsync(id, ct))
            {
                return SeriesMoveResult.Conflict("error.series.activeDownloadMove");
            }

            var trackedPaths = await db.ChapterFiles.Where(f => f.SeriesId == id)
                .Select(f => f.RelativePath).ToListAsync(ct);
            var sourceRoot = series.RootFolder.Path;
            bool TrackedPathTraversesLink(string folder) => trackedPaths
                .Where(p => LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(p), folder))
                .Any(p => LibraryPaths.Resolve(sourceRoot, LibraryPaths.ComparisonKey(p)) is { } path
                    && LibraryPaths.TraversesLink(sourceRoot, path));

            foreach (var (folder, index) in folders.Select((f, i) => (f, i)))
            {
                var target = Path.Combine(destination.Path, folder);
                if (Directory.Exists(target))
                {
                    return SeriesMoveResult.Conflict("error.series.destinationExists", new { folder = target });
                }

                // The cross-volume fallback copies and then deletes the source, so a link anywhere in
                // the tree would either pull outside files into the library or be dropped along with
                // whatever rows reach through it.
                if (Directory.Exists(Path.Combine(series.RootFolder.Path, folder))
                    && (LibraryPaths.ResolveNoLinks(series.RootFolder.Path, folder) is not { } sourceFolder
                        || (index == 0 ? LibraryPaths.ContainsLink(sourceFolder) : TrackedPathTraversesLink(folder))))
                {
                    return SeriesMoveResult.Fail("error.series.folderContainsLinks", new { folder });
                }
            }

            foreach (var (folder, index) in folders.Select((f, i) => (f, i)))
            {
                var source = Path.Combine(series.RootFolder.Path, folder);
                if (!Directory.Exists(source))
                {
                    continue;
                }

                try
                {
                    if (index == 0)
                    {
                        var target = Path.Combine(destination.Path, folder);
                        MoveDirectory(source, target);
                        undo.Add((source, target, true));
                        continue;
                    }

                    foreach (var path in trackedPaths.Where(p => LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(p), folder)))
                    {
                        var key = LibraryPaths.ComparisonKey(path);
                        if (LibraryPaths.ResolveNoLinks(series.RootFolder.Path, key) is not { } from
                            || LibraryPaths.ResolveNoLinks(destination.Path, key) is not { } to
                            || !System.IO.File.Exists(from))
                        {
                            continue;
                        }

                        CreateDirectoryTracked(Path.GetDirectoryName(to)!);
                        System.IO.File.Move(from, to);
                        undo.Add((from, to, false));
                    }

                    emptiedFolders.Add(source);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not move series folder for {Title} to {Destination}", series.Title, destination.Path);
                    RollbackMoves();

                    return SeriesMoveResult.ServerError("error.series.moveFailed");
                }
            }
        }
        else
        {
            if (!folders.Any(f => Directory.Exists(Path.Combine(destination.Path, f))))
            {
                return SeriesMoveResult.Fail("error.series.filesNotMoved", new { folder = newFolder });
            }

            // A series can span several folders. Repointing it while one is still under the old root
            // strands those chapters, so every tracked file still sitting there must also be present
            // at the destination. A file missing from both was already gone and does not block.
            var trackedPaths = await db.ChapterFiles.Where(f => f.SeriesId == id)
                .Select(f => f.RelativePath).ToListAsync(ct);
            foreach (var path in trackedPaths)
            {
                var key = LibraryPaths.ComparisonKey(path);
                if (LibraryPaths.Resolve(series.RootFolder.Path, key) is { } from && System.IO.File.Exists(from)
                    && LibraryPaths.Resolve(destination.Path, key) is var to
                    && (to is null || !System.IO.File.Exists(to)))
                {
                    return SeriesMoveResult.Fail("error.series.filesNotMoved",
                        new { folder = to ?? Path.Combine(destination.Path, key) });
                }
            }
        }

        var oldRootFolderPath = series.RootFolder.Path;
        var oldRootFolderId = series.RootFolderId;
        using (await SeriesLocks.FolderNamesAsync(CancellationToken.None))
        {
            if ((await SeriesCreationService.SeriesFoldersInRootAsync(db, destination.Id, id, CancellationToken.None))
                .Contains(series.FolderName))
            {
                RollbackMoves();
                return SeriesMoveResult.Conflict("error.series.destinationExists", new { folder = newFolder });
            }

            series.RootFolderId = destination.Id;
            var healthRows = new List<object>();
            try
            {
                // Cancellation must not be observed here: every file has already moved, so a
                // cancelled save would leave the DB pointing at the old root while the files sit
                // in the new one. CancellationToken.None keeps this write unconditional.
                await HealthInventory.MoveSeriesAsync(db, id, destination.Id, healthRows, CancellationToken.None);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                series.RootFolderId = oldRootFolderId;
                foreach (var row in healthRows)
                {
                    db.Entry(row).State = EntityState.Detached;
                }

                logger.LogError(ex, "Could not save the series move for {Title} to {Destination}", series.Title, destination.Path);
                RollbackMoves();

                return SeriesMoveResult.ServerError("error.series.moveSaveFailed", new { message = ex.Message });
            }
        }

        // Best-effort only: the move and the DB save both already succeeded, so a stray empty
        // source folder left behind is cosmetic and must not fail the request.
        foreach (var emptied in emptiedFolders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(emptied).Any())
                {
                    Directory.Delete(emptied, recursive: false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove emptied folder {Folder} after moving series {Title}", emptied, series.Title);
            }
        }

        foreach (var folder in folders)
        {
            kavitaScans.QueueScan(Path.Combine(oldRootFolderPath, folder), series.Id);
            kavitaScans.QueueScan(Path.Combine(destination.Path, folder), series.Id);
        }

        return new SeriesMoveResult(SeriesMoveFailure.None, Series: series,
            FromPath: oldRootFolderPath, ToPath: destination.Path);
    }

    private Task<bool> HasActiveDownloadAsync(int seriesId, CancellationToken ct) =>
        db.DownloadQueue.AnyAsync(q => q.SeriesId == seriesId &&
            q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
            q.Status != QueueStatus.Cancelled && q.Status != QueueStatus.Waiting, ct);

    /// <summary>
    /// <see cref="Directory.Move"/> only works within one volume — root folders routinely live on
    /// different mounts/drives, so fall back to a recursive copy + delete when the direct move
    /// fails (cross-device rename).
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Likely cross-volume; fall through to copy+delete.
        }

        try
        {
            CopyDirectory(source, destination);
        }
        catch
        {
            // The source is untouched until the copy completes, so a half-written copy can go.
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            throw;
        }

        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch
        {
            // Some of the source is already gone. The copy is complete, so restore what is missing
            // from it and drop the copy, leaving the folder whole at its old path for the caller's
            // rollback rather than split across two roots.
            RestoreMissing(destination, source);
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private static void RestoreMissing(string copy, string original)
    {
        Directory.CreateDirectory(original);
        foreach (var file in Directory.GetFiles(copy))
        {
            var target = Path.Combine(original, Path.GetFileName(file));
            if (!System.IO.File.Exists(target))
            {
                System.IO.File.Copy(file, target);
            }
        }

        foreach (var dir in Directory.GetDirectories(copy))
        {
            RestoreMissing(dir, Path.Combine(original, Path.GetFileName(dir)));
        }
    }

    private static void RefuseLink(string path)
    {
        if (LibraryPaths.IsLink(path))
        {
            throw new IOException($"Refusing to copy the symbolic link or junction {path}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            RefuseLink(file);
            System.IO.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            RefuseLink(dir);
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
