using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Keeps <see cref="HealthFile"/> rows in step with where a file lives. The inventory is keyed by
/// root folder and relative path with no foreign key to <c>RootFolders</c>, so a series move and a
/// root delete have to carry or retire the rows themselves; nothing else ever visits a row whose
/// root is gone, and its content hash then reads as a duplicate of the file's new row.
/// </summary>
public static class HealthInventory
{
    /// <summary>
    /// Repoints the series' live inventory rows at <paramref name="destinationId"/>, tracked only,
    /// so the caller's save commits them with the series. A row whose path the destination already
    /// holds cannot move (the key is unique): the row at the destination survives, takes the
    /// richer analysis state of the two, and the row at the old root is retired. Returns every
    /// entity it changed or added, so a failed save can detach them.
    /// </summary>
    public static async Task<List<object>> MoveSeriesAsync(MakiDbContext db, int seriesId, int destinationId, CancellationToken ct)
    {
        var rows = await db.HealthFiles
            .Where(f => !f.Removed && f.RootFolderId != destinationId)
            .Where(f => f.SeriesId == seriesId || db.ChapterFiles.Any(c => c.Id == f.ChapterFileId && c.SeriesId == seriesId))
            .ToListAsync(ct);
        var touched = new List<object>();
        if (rows.Count == 0) return touched;
        var paths = rows.Select(r => r.RelativePath).Distinct().ToList();
        var occupants = await db.HealthFiles
            .Where(f => f.RootFolderId == destinationId && paths.Contains(f.RelativePath))
            .ToDictionaryAsync(f => f.RelativePath, ct);
        var retired = new List<HealthFile>();
        var carriedHashes = new HashSet<string>();
        foreach (var row in rows.OrderByDescending(r => r.ContentHash != null).ThenByDescending(r => r.AnalyzedAt))
        {
            if (!occupants.TryGetValue(row.RelativePath, out var occupant))
            {
                row.RootFolderId = destinationId;
                occupants[row.RelativePath] = row;
                touched.Add(row);
                continue;
            }

            touched.Add(occupant);
            if (occupant.Removed || (occupant.ContentHash == null && row.ContentHash != null))
            {
                foreach (var finding in await db.HealthFindings.Where(f => f.FileId == occupant.Id && f.State != "resolved").ToListAsync(ct))
                {
                    finding.State = "resolved";
                    touched.Add(finding);
                }

                occupant.Version = Guid.NewGuid().ToString("N");
                occupant.Size = row.Size;
                occupant.ModifiedAt = row.ModifiedAt;
                occupant.ContentHash = row.ContentHash;
                occupant.AnalyzerVersion = row.AnalyzerVersion;
                occupant.VerifiedVersion = row.VerifiedVersion;
                occupant.Status = row.Status;
                occupant.AnalysisJson = row.AnalysisJson;
                occupant.AnalyzedAt = row.AnalyzedAt;
                var version = new HealthFileVersion
                {
                    Id = occupant.Version, FileId = occupant.Id, ContentHash = occupant.ContentHash, Size = occupant.Size,
                    ModifiedAt = occupant.ModifiedAt, RelativePath = occupant.RelativePath, AnalyzerVersion = occupant.AnalyzerVersion,
                };
                db.HealthFileVersions.Add(version);
                touched.Add(version);
                foreach (var finding in await db.HealthFindings.Where(f => f.FileId == row.Id && f.State != "resolved").ToListAsync(ct))
                {
                    var carried = new HealthFinding
                    {
                        FileId = occupant.Id, Version = occupant.Version, Kind = finding.Kind, Severity = finding.Severity,
                        MessageKey = finding.MessageKey, ParamsJson = finding.ParamsJson, Message = finding.Message, CreatedAt = finding.CreatedAt,
                    };
                    db.HealthFindings.Add(carried);
                    touched.Add(carried);
                }

                if (occupant.ContentHash != null) carriedHashes.Add(occupant.ContentHash);
            }

            occupant.SeriesId = row.SeriesId ?? seriesId;
            occupant.ChapterFileId = row.ChapterFileId;
            occupant.Removed = false;
            row.Removed = true;
            retired.Add(row);
            touched.Add(row);
        }

        if (retired.Count == 0) return touched;
        var retiredIds = retired.Select(r => r.Id).ToList();
        foreach (var finding in await db.HealthFindings.Where(f => retiredIds.Contains(f.FileId) && f.State != "resolved").ToListAsync(ct))
        {
            finding.State = "resolved";
            touched.Add(finding);
        }

        // The retired rows are still live in the database until the caller saves, so the twin
        // check leaves them out by id; a hash carried onto an occupant still has a live owner.
        var hashes = retired.Where(r => r.ContentHash != null).Select(r => r.ContentHash!).Except(carriedHashes).Distinct().ToList();
        if (hashes.Count == 0) return touched;
        var lonely = await db.HealthFindings
            .Where(x => x.Kind == "duplicate" && x.State != "resolved")
            .Where(x => db.HealthFiles.Any(f => f.Id == x.FileId && !f.Removed && !retiredIds.Contains(f.Id)
                && f.ContentHash != null && hashes.Contains(f.ContentHash)
                && !db.HealthFiles.Any(o => o.Id != f.Id && !o.Removed && !retiredIds.Contains(o.Id) && o.ContentHash == f.ContentHash)))
            .ToListAsync(ct);
        foreach (var finding in lonely)
        {
            finding.State = "resolved";
            touched.Add(finding);
        }

        return touched;
    }

    /// <summary>
    /// Marks every live row in <paramref name="rows"/> removed, resolves their open findings and
    /// resolves a <c>duplicate</c> finding whose last twin just left, since an index pass never
    /// resolves that kind on its own. Writes straight to the database; the caller owns any
    /// transaction. Returns the ids it retired.
    /// </summary>
    public static async Task<List<int>> RetireAsync(MakiDbContext db, IQueryable<HealthFile> rows, CancellationToken ct)
    {
        var live = rows.Where(f => !f.Removed);
        var ids = await live.Select(f => f.Id).ToListAsync(ct);
        if (ids.Count == 0) return ids;
        var hashes = await live.Where(f => f.ContentHash != null).Select(f => f.ContentHash!).Distinct().ToListAsync(ct);
        await live.ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true), ct);
        await db.HealthFindings.Where(f => ids.Contains(f.FileId) && f.State != "resolved")
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.State, "resolved"), ct);
        if (hashes.Count > 0)
            await db.HealthFindings
                .Where(x => x.Kind == "duplicate" && x.State != "resolved")
                .Where(x => db.HealthFiles.Any(f => f.Id == x.FileId && !f.Removed && f.ContentHash != null && hashes.Contains(f.ContentHash)
                    && !db.HealthFiles.Any(o => o.Id != f.Id && !o.Removed && o.ContentHash == f.ContentHash)))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "resolved"), ct);
        return ids;
    }
}
