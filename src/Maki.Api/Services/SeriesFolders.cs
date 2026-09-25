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
            if (LibraryPaths.TopFolder(path) is { } folder && !others.Contains(folder) && seen.Add(folder))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }
}
