using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// What pointing a root folder at a new path would find. <c>Busy</c> and <c>Warnings</c> are codes the
/// client words: busy is <c>downloads</c>, <c>imports</c>, <c>moves</c> or <c>scans</c>; a warning is
/// <c>kavitaPathMap</c>, <c>downloadPathMap</c> or <c>overlapsRoot</c>.
/// </summary>
public sealed record RootFolderPathCheck(
    int Id,
    string OldPath,
    string Path,
    int Series,
    int FoldersFound,
    int FoldersMissing,
    IReadOnlyList<string> MissingFolders,
    IReadOnlyList<string> Busy,
    IReadOnlyList<string> Warnings);

/// <summary>A refusal as a server catalogue key. <c>Conflict</c> answers 409 rather than 400.</summary>
public sealed record RootFolderPathRefusal(string Key, object? Args = null, bool Conflict = false);

public sealed record RootFolderPathOutcome(
    bool NotFound = false, RootFolderPathRefusal? Refusal = null, RootFolderPathCheck? Check = null);

/// <summary>
/// Changes the path a root folder points at, for a re-mounted share or a NAS migration. Everything
/// Maki stores under a root is relative to it (<see cref="Series.FolderName"/>,
/// <see cref="ChapterFile.RelativePath"/>, the health inventory, upgrade and conversion trash), so
/// the change is the row itself plus the recycle bin, which keeps the absolute root a file was
/// binned from. Nothing on disk is moved: the files must already be at the new path.
/// </summary>
public sealed class RootFolderPathService(
    MakiDbContext db, SeriesBulkOperations bulk, ReaderArchiveCache archives, IAppSettings settings)
{
    private const int MissingShown = 20;

    private static readonly QueueStatus[] Working =
    [
        QueueStatus.FetchingPages, QueueStatus.Downloading, QueueStatus.Validating,
        QueueStatus.Packaging, QueueStatus.Importing,
    ];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<RootFolderPathOutcome> CheckAsync(int id, string? path, CancellationToken ct)
    {
        var root = await db.RootFolders.FindAsync([id], ct);
        if (root is null)
        {
            return new RootFolderPathOutcome(NotFound: true);
        }

        if (Normalize(path) is not { } full)
        {
            return Refuse(string.IsNullOrWhiteSpace(path) ? "error.rootFolder.pathRequired" : "error.rootFolder.doesNotExist",
                string.IsNullOrWhiteSpace(path) ? null : new { path });
        }

        if (!Directory.Exists(full))
        {
            return Refuse("error.rootFolder.doesNotExist", new { path = full });
        }

        HashSet<string> present;
        try
        {
            present = Directory.EnumerateDirectories(full)
                .Select(d => System.IO.Path.GetFileName(d))
                .ToHashSet(LibraryPaths.FolderComparer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse("error.rootFolder.notReadable", new { path = full });
        }

        var others = await db.RootFolders.Where(f => f.Id != id).Select(f => f.Path).ToListAsync(ct);
        if (others.Any(p => SamePath(p, full)))
        {
            return new RootFolderPathOutcome(Refusal: new RootFolderPathRefusal("error.rootFolder.alreadyExists", Conflict: true));
        }

        // Archived series still own a folder under the root, so they count too.
        var folders = await db.Series.IgnoreQueryFilters()
            .Where(s => s.RootFolderId == id)
            .Select(s => s.FolderName)
            .ToListAsync(ct);
        var missing = folders.Where(f => !present.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var warnings = new List<string>();
        if (await PrefixLostAsync(SettingKeys.KavitaPathMapFrom, root.Path, full, ct))
        {
            warnings.Add("kavitaPathMap");
        }

        if (await PrefixLostAsync(SettingKeys.DownloadClientPathMapTo, root.Path, full, ct, mapUnderRoot: true))
        {
            warnings.Add("downloadPathMap");
        }

        if (others.Any(p => Under(p, full) || Under(full, p)))
        {
            warnings.Add("overlapsRoot");
        }

        return new RootFolderPathOutcome(Check: new RootFolderPathCheck(
            id, root.Path, full, folders.Count, folders.Count - missing.Count, missing.Count,
            missing.Take(MissingShown).ToList(), await BusyAsync(id, ct), warnings));
    }

    public async Task<RootFolderPathOutcome> ApplyAsync(int id, string? path, CancellationToken ct)
    {
        await HealthOperationService.MutationGate.WaitAsync(ct);
        try
        {
            using var names = await SeriesLocks.FolderNamesAsync(ct);
            var outcome = await CheckAsync(id, path, ct);
            if (outcome.Check is not { } check || check.Busy.Count > 0)
            {
                return outcome;
            }

            var root = await db.RootFolders.SingleAsync(f => f.Id == id, ct);
            var oldPath = root.Path;
            if (oldPath == check.Path)
            {
                return outcome;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            root.Path = check.Path;
            var binned = await db.RecycleBin.Where(e => e.RootFolderId == id).ToListAsync(ct);
            foreach (var entry in binned)
            {
                if (SamePath(entry.RootPath, oldPath))
                {
                    entry.RootPath = check.Path;
                }

                if (Rebase(entry.OriginalPath, oldPath, check.Path) is { } moved)
                {
                    entry.OriginalPath = moved;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            // Page lists are keyed by file id and stay right, but the open archive handles are on the old path.
            var fileIds = await db.ChapterFiles.IgnoreQueryFilters()
                .Where(f => db.Series.IgnoreQueryFilters().Any(s => s.Id == f.SeriesId && s.RootFolderId == id))
                .Select(f => f.Id)
                .ToListAsync(CancellationToken.None);
            foreach (var fileId in fileIds)
            {
                archives.Invalidate(fileId);
            }

            HealthWriteProbe.Shared.Expire();
            return outcome;
        }
        finally
        {
            HealthOperationService.MutationGate.Release();
        }
    }

    private async Task<List<string>> BusyAsync(int id, CancellationToken ct)
    {
        var busy = new List<string>();
        var series = db.Series.IgnoreQueryFilters().Where(s => s.RootFolderId == id);
        if (await db.DownloadQueue.AnyAsync(q => Working.Contains(q.Status) && series.Any(s => s.Id == q.SeriesId), ct))
        {
            busy.Add("downloads");
        }

        if (await series.AnyAsync(s => s.PendingImportLink != PendingImportLink.None, ct))
        {
            busy.Add("imports");
        }

        if (bulk.HasActiveFileOperation())
        {
            busy.Add("moves");
        }

        var files = db.HealthFiles.Where(f => f.RootFolderId == id);
        if (await db.HealthScans.AnyAsync(s => s.RootFolderId == id && (s.Status == "pending" || s.Status == "running"), ct)
            || await db.HealthOperations.AnyAsync(o => o.Status != "completed" && o.Status != "failed"
                && o.Status != "cancelled" && files.Any(f => f.Id == o.FileId), ct))
        {
            busy.Add("scans");
        }

        return busy;
    }

    /// <summary>
    /// A path setting that names a folder under the old root but not under the new one. Kavita's
    /// "from" is the Maki side of its mapping; the download client's "to" is where Maki reads
    /// finished downloads, which only matters when it sits inside this root.
    /// </summary>
    private async Task<bool> PrefixLostAsync(string key, string oldPath, string newPath, CancellationToken ct,
        bool mapUnderRoot = false)
    {
        var value = (await settings.GetAsync(key, ct))?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return mapUnderRoot
            ? Under(value, oldPath) && !Under(value, newPath)
            : Under(oldPath, value) && !Under(newPath, value);
    }

    private static RootFolderPathOutcome Refuse(string key, object? args = null) =>
        new(Refusal: new RootFolderPathRefusal(key, args));

    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b),
            PathComparison);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    internal static bool Under(string path, string folder)
    {
        var p = System.IO.Path.TrimEndingDirectorySeparator(path);
        var f = System.IO.Path.TrimEndingDirectorySeparator(folder);
        return string.Equals(p, f, PathComparison)
            || (p.Length > f.Length && p.StartsWith(f, PathComparison)
                && (p[f.Length] == System.IO.Path.DirectorySeparatorChar || p[f.Length] == System.IO.Path.AltDirectorySeparatorChar));
    }

    internal static string? Rebase(string path, string oldRoot, string newRoot)
    {
        if (!Under(path, oldRoot))
        {
            return null;
        }

        var rest = System.IO.Path.TrimEndingDirectorySeparator(path)[System.IO.Path.TrimEndingDirectorySeparator(oldRoot).Length..]
            .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return rest.Length == 0 ? newRoot : System.IO.Path.Combine(newRoot, rest);
    }
}
