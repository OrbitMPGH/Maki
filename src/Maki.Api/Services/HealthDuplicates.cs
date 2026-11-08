using Maki.Core.Entities;
using Maki.Core.Io;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Byte-identical archives as sets of copies. A <c>duplicate</c> finding sits on every copy, so the
/// page needs to know which one to keep: the copies that back chapters, or the oldest row when none
/// does. Every other copy on disk is a spare, and deleting a spare loses nothing.
/// </summary>
public static class HealthDuplicates
{
    /// <param name="KeptOnDisk">Some kept copy of the set is on disk. Without one nothing is spare: the
    /// copies left are where the missing kept copy's chapters can move.</param>
    public sealed record Copy(int Id, string Version, int RootFolderId, string RelativePath, long Size,
        int? ChapterFileId, string ContentHash, bool Kept, bool KeptOnDisk)
    {
        public bool Missing => Size < 0;
        public bool Spare => !Kept && !Missing && KeptOnDisk;
    }

    /// <summary>Every live copy of each hash, kept copies first, then by path.</summary>
    public static async Task<Dictionary<string, List<Copy>>> GroupsAsync(MakiDbContext db, IQueryable<string> hashes, CancellationToken ct)
    {
        var rows = await db.HealthFiles.AsNoTracking()
            .Where(f => !f.Removed && f.ContentHash != null && hashes.Contains(f.ContentHash))
            .Select(f => new { f.Id, f.Version, f.RootFolderId, f.RelativePath, f.Size, f.ChapterFileId, ContentHash = f.ContentHash! })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ContentHash).ToDictionary(g => g.Key, g =>
        {
            var linked = g.Any(r => r.ChapterFileId != null);
            var keep = linked ? (int?)null : g.Where(r => r.Size >= 0).Select(r => (int?)r.Id).Min();
            var keptOnDisk = linked ? g.Any(r => r.ChapterFileId != null && r.Size >= 0) : keep != null;
            return g
                .Select(r => new Copy(r.Id, r.Version, r.RootFolderId, r.RelativePath, r.Size, r.ChapterFileId, r.ContentHash,
                    linked ? r.ChapterFileId != null : r.Id == keep, keptOnDisk))
                .OrderByDescending(c => c.Kept)
                .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    /// <summary>
    /// The copy a deleted file's chapters can move to: same bytes, on disk, in the same root and the
    /// same series folder, and backing nothing itself. A copy elsewhere would leave a chapter file
    /// outside the series' folders, and a copy that already backs chapters would have two files'
    /// worth of chapters on one row. It must also provably be another file (<paramref name="sameFile"/>,
    /// <see cref="FileIdentity.Compare"/> by default), the same rule apply checks, so a preview never
    /// promises a relink apply would refuse.
    /// </summary>
    public static async Task<HealthFile?> RelinkTargetAsync(MakiDbContext db, HealthFile file, CancellationToken ct,
        Func<string, string, FileSameness>? sameFile = null)
    {
        if (file.ContentHash == null || LibraryPaths.TopFolder(file.RelativePath) is not { } folder) return null;
        var rootPath = await db.RootFolders.Where(r => r.Id == file.RootFolderId).Select(r => r.Path).FirstOrDefaultAsync(ct);
        if (rootPath == null || LibraryPaths.Resolve(rootPath, file.RelativePath) is not { } path) return null;
        var candidates = await db.HealthFiles
            .Where(f => f.Id != file.Id && !f.Removed && f.Size >= 0 && f.Status != "pending" && f.ChapterFileId == null
                && f.RootFolderId == file.RootFolderId && f.ContentHash == file.ContentHash)
            .OrderBy(f => f.Id)
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            if (!LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(candidate.RelativePath), folder)) continue;
            if ((await HealthInventory.RecordsAtAsync(db, file.RootFolderId, candidate.RelativePath, ct)).Count > 0) continue;
            if (LibraryPaths.Resolve(rootPath, candidate.RelativePath) is { } candidatePath
                && (sameFile ?? FileIdentity.Compare)(path, candidatePath) == FileSameness.Different) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Resolves every open <c>duplicate</c> finding whose file no longer has a live twin, for the
    /// given hashes or, with null, the whole inventory. Nothing else closes one once its twin is
    /// deleted, because an index pass never resolves that kind.
    /// </summary>
    public static Task<int> ResolveLonelyAsync(MakiDbContext db, IReadOnlyCollection<string>? hashes, CancellationToken ct)
    {
        var files = db.HealthFiles.Where(f => !f.Removed && f.ContentHash != null);
        if (hashes != null) files = files.Where(f => hashes.Contains(f.ContentHash!));
        var lonely = files.Where(f => !db.HealthFiles.Any(o => o.Id != f.Id && !o.Removed && o.ContentHash == f.ContentHash));
        return db.HealthFindings
            .Where(x => x.Kind == "duplicate" && x.State != "resolved" && lonely.Any(f => f.Id == x.FileId))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "resolved"), ct);
    }
}
