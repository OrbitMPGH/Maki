using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Deletes chapter files on purpose: the file goes, the chapter rows and everyone's reads stay, and
/// <see cref="Chapter.FileRemovedAt"/> keeps the chapters from being downloaded again. Shared by the
/// chapter table's "Delete file", the Files tab and automatic cleanup so all three agree on what a
/// deletion leaves behind and on when a file is too shared to touch.
/// </summary>
public class ChapterFileDeletion(
    MakiDbContext db,
    ReaderArchiveCache archives,
    TimeProvider time,
    ILogger<ChapterFileDeletion> logger)
{
    public sealed record Result(int Deleted, int Kept, int Failed, int ChaptersRemoved);

    /// <summary>
    /// Which of <paramref name="absolutePaths"/> another <see cref="ChapterFile"/> row still points at,
    /// ignoring the rows in <paramref name="leaving"/>. Compared as absolute paths across every root
    /// folder, because two roots can nest (<c>/manga</c> and <c>/manga/shonen</c>) and name one file
    /// by two different relative paths. Query filters are off: a row in a root folder the current
    /// user cannot see still holds the file.
    /// </summary>
    public async Task<HashSet<string>> ClaimedAsync(
        IReadOnlyCollection<string> absolutePaths, IReadOnlySet<int> leaving, CancellationToken ct)
    {
        var claimed = new HashSet<string>(LibraryPaths.FolderComparer);
        if (absolutePaths.Count == 0)
        {
            return claimed;
        }

        var wanted = absolutePaths.ToHashSet(LibraryPaths.FolderComparer);
        var names = absolutePaths.Select(Path.GetFileName).OfType<string>().ToHashSet(LibraryPaths.FolderComparer);
        var roots = await db.RootFolders.IgnoreQueryFilters().AsNoTracking()
            .ToDictionaryAsync(r => r.Id, r => r.Path, ct);
        var rows = await (from f in db.ChapterFiles.IgnoreQueryFilters()
                          join s in db.Series.IgnoreQueryFilters() on f.SeriesId equals s.Id
                          select new { f.Id, f.RelativePath, s.RootFolderId })
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (leaving.Contains(row.Id) ||
                !names.Contains(Path.GetFileName(LibraryPaths.ComparisonKey(row.RelativePath))) ||
                !roots.TryGetValue(row.RootFolderId, out var rootPath) ||
                LibraryPaths.Resolve(rootPath, row.RelativePath) is not { } absolute)
            {
                continue;
            }

            if (wanted.Contains(absolute))
            {
                claimed.Add(absolute);
            }
        }

        return claimed;
    }

    /// <summary>A file about to lose its row. <see cref="AbsolutePath"/> is null when it resolves outside the root or through a linked folder.</summary>
    public sealed record DiskTarget(string RelativePath, string? AbsolutePath, bool Claimed)
    {
        public bool Deletable => AbsolutePath is not null && !Claimed;
    }

    /// <summary>
    /// Resolves <paramref name="relativePaths"/> for deletion and checks each against
    /// <see cref="ClaimedAsync"/>. For callers that remove rows outright: they save the rows first
    /// and only then pass each <see cref="DiskTarget.Deletable"/> path to <see cref="DeleteFromDisk"/>,
    /// so a failed save never leaves rows behind for files already gone.
    /// </summary>
    public async Task<List<DiskTarget>> TargetsAsync(
        string rootPath, IEnumerable<string> relativePaths, IReadOnlySet<int> leaving, CancellationToken ct)
    {
        var resolved = relativePaths
            .Select(p => (Relative: p, Absolute: LibraryPaths.ResolveForDelete(rootPath, p)))
            .ToList();
        var claimed = await ClaimedAsync(resolved.Select(r => r.Absolute).OfType<string>().ToList(), leaving, ct);
        return resolved
            .Select(r => new DiskTarget(r.Relative, r.Absolute, r.Absolute is not null && claimed.Contains(r.Absolute)))
            .ToList();
    }

    /// <summary>Deletes one file resolved by <see cref="TargetsAsync"/>. A folder already gone counts as deleted.</summary>
    public bool DeleteFromDisk(string absolutePath)
    {
        try
        {
            File.Delete(absolutePath);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {File}", absolutePath);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Deletes <paramref name="files"/>, all belonging to <paramref name="series"/>, and marks every
    /// chapter on them removed. The caller holds the series lock. A file another row still claims
    /// (<see cref="ClaimedAsync"/>) stays on disk: this series lets go of it and the other keeps it.
    /// A file that can't be deleted keeps its row and links, so the table never says a file is gone
    /// that is still there.
    /// </summary>
    public async Task<Result> DeleteAsync(Series series, IReadOnlyList<ChapterFile> files, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return new Result(0, 0, 0, 0);
        }

        if (series.RootFolder is null)
        {
            return new Result(0, 0, files.Count, 0);
        }

        var fileIds = files.Select(f => f.Id).ToList();
        var linked = (await db.Chapters
                .Where(c => c.ChapterFileId != null && fileIds.Contains(c.ChapterFileId.Value))
                .ToListAsync(ct))
            .ToLookup(c => c.ChapterFileId!.Value);

        var resolved = files.ToDictionary(f => f.Id, f => LibraryPaths.ResolveForDelete(series.RootFolder.Path, f.RelativePath));
        var claimed = await ClaimedAsync(
            resolved.Values.OfType<string>().ToList(), fileIds.ToHashSet(), ct);

        var now = time.GetUtcNow().UtcDateTime;
        int deleted = 0, kept = 0, failed = 0, chaptersRemoved = 0;
        foreach (var file in files)
        {
            // Resolve, never a bare Combine: RelativePath is stored data, and a row that escapes the
            // root would have this delete an arbitrary file.
            if (resolved[file.Id] is not { } absolute)
            {
                logger.LogWarning("Refusing to delete {File}: resolves outside {Root} or through a linked folder",
                    file.RelativePath, series.RootFolder.Path);
                failed++;
                continue;
            }

            if (claimed.Contains(absolute))
            {
                logger.LogInformation("Kept {File} on disk: another series' record still points at it", absolute);
                kept++;
            }
            else if (DeleteFromDisk(absolute))
            {
                deleted++;
            }
            else
            {
                failed++;
                continue;
            }

            foreach (var chapter in linked[file.Id])
            {
                chapter.ChapterFileId = null;
                chapter.FileRemovedAt = now;
                chaptersRemoved++;
            }

            archives.Invalidate(file.Id);
            db.ChapterFiles.Remove(file);
        }

        // The disk has already changed, so an aborted request or a shutdown must not leave rows pointing at deleted files.
        await db.SaveChangesAsync(CancellationToken.None);
        return new Result(deleted, kept, failed, chaptersRemoved);
    }
}
