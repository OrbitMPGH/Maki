using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Where replaced files wait before they are purged: <c>&lt;root&gt;/.maki-trash/&lt;SeriesId&gt;/</c>,
/// on the same volume as the library so putting a file aside is a rename, never a copy.
/// </summary>
public static class UpgradeTrash
{
    public const string FolderName = ".maki-trash";

    private static readonly TimeSpan[] MoveBackoff =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    public static string SeriesFolder(string rootPath, int seriesId) =>
        Path.Combine(rootPath, FolderName, seriesId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates the series' trash folder, and hides the trash root on Windows.</summary>
    public static void EnsureFolder(string rootPath, int seriesId)
    {
        Directory.CreateDirectory(SeriesFolder(rootPath, seriesId));
        if (OperatingSystem.IsWindows())
        {
            var root = new DirectoryInfo(Path.Combine(rootPath, FolderName));
            if (!root.Attributes.HasFlag(FileAttributes.Hidden))
            {
                root.Attributes |= FileAttributes.Hidden;
            }
        }
    }

    /// <summary>
    /// <c>.maki-trash/&lt;seriesId&gt;/&lt;prefix&gt;-&lt;name&gt;</c>, relative to the root with forward
    /// slashes. A number goes after the prefix when that name is already taken, since a file upgraded
    /// twice inside the retention window would otherwise collide with its own earlier copy.
    /// </summary>
    public static string NewRelativePath(string rootPath, int seriesId, string prefix, string name)
    {
        var folder = $"{FolderName}/{seriesId.ToString(CultureInfo.InvariantCulture)}";
        var candidate = $"{folder}/{prefix}-{name}";
        for (var n = 2; File.Exists(LibraryPaths.Resolve(rootPath, candidate)); n++)
        {
            candidate = $"{folder}/{prefix}-{n.ToString(CultureInfo.InvariantCulture)}-{name}";
        }

        return candidate;
    }

    /// <summary>Only an archive can be replaced by the zip the downloader packages; a PDF cannot.</summary>
    public static bool IsReplaceable(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() is ".cbz" or ".zip";

    /// <summary>
    /// A rename into the trash, retried three times with a short back-off because the reader or Kavita
    /// may be holding the file for a moment. False when it still failed; the source is then untouched.
    /// <para>
    /// A rename keeps the file's old timestamps, so the moved file's write time is set to now: that is
    /// what the purge ages trash by. Metadata only, so a hardlinked copy keeps its bytes.
    /// </para>
    /// </summary>
    public static async Task<bool> MoveIntoTrashAsync(string from, string to, ILogger logger, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                try
                {
                    File.SetLastWriteTimeUtc(to, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not stamp {Path} with its trash time", to);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MoveBackoff.Length)
                {
                    logger.LogWarning(ex, "Could not move {From} to {To}", from, to);
                    return false;
                }

                await Task.Delay(MoveBackoff[attempt], ct);
            }
        }
    }
}

public static class UpgradeHistoryStates
{
    /// <summary>Reverted and trash-available for each history id, in one query.</summary>
    public static async Task<Dictionary<int, Maki.Api.Dtos.UpgradeHistoryState>> LoadAsync(
        MakiDbContext db, IReadOnlyCollection<int> historyIds, CancellationToken ct)
    {
        if (historyIds.Count == 0)
        {
            return [];
        }

        var rows = await db.UpgradeHistory.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id))
            .Select(h => new
            {
                h.Id,
                h.RevertedAtUtc,
                h.TrashPath,
                RootPath = db.Series.Where(s => s.Id == h.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault()
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => new Maki.Api.Dtos.UpgradeHistoryState(
            r.RevertedAtUtc is not null,
            r.RevertedAtUtc is null && r.TrashPath is { } trash && r.RootPath is { } root &&
            LibraryPaths.Resolve(root, trash) is { } path && File.Exists(path)));
    }

    /// <summary>
    /// The same for torrent replacement groups: reverted once every row is, and revertible while none
    /// is and every row's trash file is still there.
    /// </summary>
    public static async Task<Dictionary<Guid, Maki.Api.Dtos.UpgradeHistoryState>> LoadGroupsAsync(
        MakiDbContext db, IReadOnlyCollection<Guid> groupIds, CancellationToken ct)
    {
        if (groupIds.Count == 0)
        {
            return [];
        }

        var rows = await db.UpgradeHistory.AsNoTracking()
            .Where(h => h.GroupId != null && groupIds.Contains(h.GroupId.Value))
            .Select(h => new
            {
                GroupId = h.GroupId!.Value,
                h.RevertedAtUtc,
                h.TrashPath,
                RootPath = db.Series.Where(s => s.Id == h.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault()
            })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.GroupId).ToDictionary(g => g.Key, g => new Maki.Api.Dtos.UpgradeHistoryState(
            g.All(r => r.RevertedAtUtc is not null),
            g.All(r => r.RevertedAtUtc is null && r.TrashPath is { } trash && r.RootPath is { } root &&
                       LibraryPaths.Resolve(root, trash) is { } path && File.Exists(path))));
    }
}

/// <summary>Reports and purges what <see cref="UpgradeTrash"/> holds.</summary>
public class UpgradeTrashService(MakiDbContext db, IAppSettings settings, ILogger<UpgradeTrashService> logger)
{
    public async Task<(long Bytes, int Files)> SizeAsync(CancellationToken ct)
    {
        // A reverted group points every row at the one volume copy it put aside, so count paths, not rows.
        var rows = await db.UpgradeHistory.AsNoTracking()
            .Where(h => h.TrashPath != null)
            .Select(h => new { h.SeriesId, h.TrashPath, h.TrashBytes })
            .ToListAsync(ct);
        var files = rows.GroupBy(r => (r.SeriesId, r.TrashPath)).Select(g => g.Max(r => r.TrashBytes)).ToList();
        return (files.Sum(), files.Count);
    }

    /// <summary>
    /// Deletes trash older than <c>upgrades.trashRetentionDays</c>: files history rows still point at
    /// (clearing those rows' <c>TrashPath</c>), then anything else under a root's trash folder, then
    /// the series folders that leaves empty. Returns how many files went.
    /// </summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var options = await UpgradeOptions.LoadAsync(settings, ct);
        var cutoff = DateTime.UtcNow.AddDays(-options.TrashRetentionDays);
        var purged = 0;

        var roots = await db.Series.IgnoreQueryFilters()
            .Select(s => new { s.Id, s.RootFolder!.Path })
            .ToDictionaryAsync(s => s.Id, s => s.Path, ct);
        var expired = await db.UpgradeHistory.IgnoreQueryFilters()
            .Where(h => h.TrashPath != null && (h.RevertedAtUtc ?? h.CreatedAtUtc) < cutoff)
            .ToListAsync(ct);
        foreach (var row in expired)
        {
            if (roots.TryGetValue(row.SeriesId, out var rootPath) &&
                LibraryPaths.Resolve(rootPath, row.TrashPath!) is { } path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        purged++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not purge upgrade trash {Path}", path);
                    continue;
                }
            }

            row.TrashPath = null;
            row.TrashBytes = 0;
        }

        await db.SaveChangesAsync(ct);

        var referenced = await db.UpgradeHistory.IgnoreQueryFilters()
            .Where(h => h.TrashPath != null)
            .Select(h => new { h.SeriesId, h.TrashPath })
            .ToListAsync(ct);
        var keep = referenced
            .Where(r => roots.ContainsKey(r.SeriesId))
            .Select(r => LibraryPaths.Resolve(roots[r.SeriesId], r.TrashPath!))
            .OfType<string>()
            .Select(Path.GetFullPath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var rootPath in await db.RootFolders.Select(r => r.Path).ToListAsync(ct))
        {
            var trash = Path.Combine(rootPath, UpgradeTrash.FolderName);
            if (!Directory.Exists(trash))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!keep.Contains(Path.GetFullPath(file)) && File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        purged++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not purge upgrade trash {Path}", file);
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(trash))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not remove empty trash folder {Dir}", dir);
                }
            }
        }

        return purged;
    }
}
