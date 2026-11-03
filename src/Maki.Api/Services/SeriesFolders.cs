using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Every root-level folder a series has files in. Usually just <see cref="Series.FolderName"/>,
/// but an import under the keep-new-standard naming mode leaves the adopted files in the original
/// folder while <see cref="Series.FolderName"/> names the standard one future downloads go to.
/// </summary>
public static class SeriesFolders
{
    /// <summary>
    /// <see cref="Series.FolderName"/> first, then the other folders its files sit in. A folder that
    /// is another series' own folder is left out even when a manual link points into it, so a
    /// rescan, delete or move of this series never reaches into that one.
    /// </summary>
    public static async Task<List<string>> ForAsync(MakiDbContext db, Series series, CancellationToken ct)
    {
        var paths = await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id)
            .Select(f => f.RelativePath)
            .ToListAsync(ct);
        var others = (await db.Series
                .Where(s => s.RootFolderId == series.RootFolderId && s.Id != series.Id)
                .Select(s => s.FolderName)
                .ToListAsync(ct))
            .ToHashSet(LibraryPaths.FolderComparer);

        var folders = new List<string> { series.FolderName };
        var seen = new HashSet<string>(folders, LibraryPaths.FolderComparer);
        foreach (var path in paths)
        {
            if (LibraryPaths.TopFolder(path) is { } folder && folder is not ("" or "." or "..") &&
                !others.Contains(folder) && seen.Add(folder))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    /// <summary>
    /// Whether <see cref="Series.FolderName"/> is also another series' folder, or holds another
    /// series' files. Older data can give two series one folder, and a delete or prune of it must
    /// then leave the other series' part alone. Case is ignored on every host, since a
    /// case-insensitive share under Docker holds "One Piece" and "one piece" as one.
    /// </summary>
    public static async Task<bool> SharedAsync(MakiDbContext db, Series series, CancellationToken ct)
    {
        if ((await SeriesCreationService.SeriesFoldersInRootAsync(db, series.RootFolderId, series.Id, ct))
            .Contains(series.FolderName))
        {
            return true;
        }

        var pattern = series.FolderName.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        return (await db.ChapterFiles
                .Where(f => f.SeriesId != series.Id &&
                            db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == series.RootFolderId) &&
                            EF.Functions.Like(f.RelativePath, pattern, @"\"))
                .Select(f => f.RelativePath)
                .ToListAsync(ct))
            .Any(p => LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(p), series.FolderName));
    }

    /// <summary>
    /// Removes the empty folders under <paramref name="folder"/>, deepest first, then the folder
    /// itself once nothing but <paramref name="makiCover"/> (Maki's own cover.jpg) is left in it.
    /// Never recursive: anything else keeps its folder.
    /// </summary>
    /// <returns>How many entries kept <paramref name="folder"/>; 0 when it was removed.</returns>
    public static int PruneTree(string folder, string? makiCover)
    {
        foreach (var dir in LibraryPaths.EnumerateDirectoriesNoLinks(folder).OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }

        var left = Directory.EnumerateFileSystemEntries(folder).ToList();
        if (left.Any(p => makiCover is null || !LibraryPaths.FolderComparer.Equals(p, makiCover) || Directory.Exists(p)))
        {
            return left.Count;
        }

        foreach (var cover in left)
        {
            File.Delete(cover);
        }

        Directory.Delete(folder, recursive: false);
        return 0;
    }

    /// <summary>Removes <paramref name="dir"/> only when nothing at all is in it.</summary>
    public static void RemoveIfEmpty(string dir)
    {
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir, recursive: false);
        }
    }
}
