using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One-time repair of files adopted by a library import under the keep-new-standard folder naming
/// mode before the linker used the folder the files were actually in. Those rows were stored under
/// <see cref="Series.FolderName"/> (the standard folder, which the import never creates) while the
/// files stayed in the original folder, so the reader, rescan and delete all looked in the wrong
/// place and the import page kept offering the original folder again.
/// <para>
/// The original folder name was never recorded, so it is found on disk: the one root-level folder,
/// not owned by any series, that holds the most of the missing files under the same names. A tie
/// or no hit leaves the rows alone. Marker-gated like <see cref="SeriesIdentityRepairService"/>, and
/// the marker is only written once every root folder was reachable, so an unmounted share at
/// startup does not burn the one chance to run.
/// </para>
/// </summary>
public class ImportPathRepairService(MakiDbContext db, ILogger<ImportPathRepairService> logger)
{
    public const string MarkerKey = "library.importPathRepairDone";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var roots = await db.RootFolders.ToListAsync(ct);
        if (roots.Any(r => !Directory.Exists(r.Path)))
        {
            logger.LogInformation("Import path repair postponed: a root folder is not reachable");
            return;
        }

        var repaired = 0;
        foreach (var root in roots)
        {
            repaired += await RepairRootAsync(root, ct);
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);

        if (repaired > 0)
        {
            logger.LogInformation("Import path repair complete: repointed {Count} imported file(s)", repaired);
        }
    }

    private async Task<int> RepairRootAsync(RootFolder root, CancellationToken ct)
    {
        var series = await db.Series.Where(s => s.RootFolderId == root.Id).ToListAsync(ct);
        var seriesIds = series.Select(s => s.Id).ToList();
        var imported = await db.ChapterFiles
            .Where(f => seriesIds.Contains(f.SeriesId) && f.SourceName == "import")
            .ToListAsync(ct);
        if (imported.Count == 0)
        {
            return 0;
        }

        var owned = series.Select(s => s.FolderName).ToHashSet(LibraryPaths.FolderComparer);
        var unowned = Directory.GetDirectories(root.Path)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !owned.Contains(name))
            .ToList();

        var repaired = 0;
        foreach (var s in series)
        {
            var missing = imported
                .Where(f => f.SeriesId == s.Id
                            && LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(f.RelativePath), s.FolderName)
                            && LibraryPaths.Resolve(root.Path, f.RelativePath) is { } path && !File.Exists(path))
                .Select(f => (File: f, Tail: f.RelativePath[(s.FolderName.Length + 1)..]))
                .ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            // Narrow on the first file, then score the survivors on the rest: a single name like
            // "Vol. 01.cbz" can sit in many folders, the whole set rarely does.
            var candidates = unowned
                .Where(folder => File.Exists(Path.Combine(root.Path, folder, missing[0].Tail)))
                .Select(folder => (Folder: folder, Hits: missing.Count(m => File.Exists(Path.Combine(root.Path, folder, m.Tail)))))
                .OrderByDescending(c => c.Hits)
                .ToList();
            if (candidates.Count == 0 || (candidates.Count > 1 && candidates[1].Hits == candidates[0].Hits))
            {
                continue;
            }

            var folderName = candidates[0].Folder;
            foreach (var (file, tail) in missing)
            {
                if (File.Exists(Path.Combine(root.Path, folderName, tail)))
                {
                    file.RelativePath = Path.Combine(folderName, tail);
                    repaired++;
                }
            }

            logger.LogInformation("Repointed imported files of {Title} to '{Folder}'", s.Title, folderName);
        }

        await db.SaveChangesAsync(ct);
        return repaired;
    }
}
