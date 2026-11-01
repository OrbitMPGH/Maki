using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Keeps <see cref="HealthFile"/> rows in step with where a file lives. Rows have no foreign key to
/// <c>RootFolders</c>, so a series move and a root delete carry or retire them here.
/// </summary>
public static class HealthInventory
{
    private static readonly StringComparer KeyComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static HealthFileVersion VersionOf(HealthFile file) => new()
    {
        Id = file.Version, FileId = file.Id, ContentHash = file.ContentHash, Size = file.Size,
        ModifiedAt = file.ModifiedAt, RelativePath = file.RelativePath, AnalyzerVersion = file.AnalyzerVersion,
    };

    /// <summary>
    /// Repoints the series' live rows at <paramref name="destinationId"/> without saving. A path the
    /// destination already holds keeps that row and retires the moving one. Every entity it attaches,
    /// changes or adds goes into <paramref name="touched"/>, also when it throws, so the caller can detach them.
    /// </summary>
    public static async Task MoveSeriesAsync(MakiDbContext db, int seriesId, int destinationId, List<object> touched, CancellationToken ct)
    {
        var rows = await AttachAsync(db, db.HealthFiles
            .Where(f => !f.Removed && f.RootFolderId != destinationId)
            .Where(f => f.SeriesId == seriesId || db.ChapterFiles.Any(c => c.Id == f.ChapterFileId && c.SeriesId == seriesId)), touched, ct);
        if (rows.Count == 0) return;
        var spellings = rows.SelectMany(r => new[] { r.RelativePath, r.RelativePath.Replace('\\', '/'), r.RelativePath.Replace('/', '\\') })
            .Distinct().ToList();
        var occupants = (await AttachAsync(db, db.HealthFiles
                .Where(f => f.RootFolderId == destinationId && spellings.Contains(f.RelativePath)), touched, ct))
            .GroupBy(f => LibraryPaths.ComparisonKey(f.RelativePath), KeyComparer)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Removed).ThenBy(f => f.Id).First(), KeyComparer);
        var colliding = rows.Where(r => occupants.ContainsKey(LibraryPaths.ComparisonKey(r.RelativePath))).Select(r => r.Id).ToList();
        var analyses = await db.HealthFiles.AsNoTracking()
            .Where(f => colliding.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.AnalysisJson, ct);
        var fileIds = rows.Select(r => r.Id).Concat(occupants.Values.Select(o => o.Id)).ToList();
        var openFindings = (await db.HealthFindings.Where(f => fileIds.Contains(f.FileId) && f.State != "resolved").ToListAsync(ct))
            .ToLookup(f => f.FileId);
        touched.AddRange(openFindings.SelectMany(g => g));

        var retired = new List<HealthFile>();
        var carriedHashes = new HashSet<string>();
        foreach (var row in rows.OrderByDescending(r => r.ContentHash != null).ThenByDescending(r => r.AnalyzedAt))
        {
            var key = LibraryPaths.ComparisonKey(row.RelativePath);
            if (!occupants.TryGetValue(key, out var occupant))
            {
                row.RootFolderId = destinationId;
                occupants[key] = row;
                continue;
            }

            if (occupant.Removed || (occupant.ContentHash == null && row.ContentHash != null))
            {
                foreach (var finding in openFindings[occupant.Id]) finding.State = "resolved";
                TakeAnalysis(occupant, row, analyses.GetValueOrDefault(row.Id, row.AnalysisJson));
                var version = VersionOf(occupant);
                db.HealthFileVersions.Add(version);
                touched.Add(version);
                foreach (var finding in openFindings[row.Id])
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

            // A live occupant already linked keeps its link unless the moving row brings a real one.
            if (occupant.Removed || row.ChapterFileId != null)
            {
                occupant.ChapterFileId = row.ChapterFileId;
                occupant.SeriesId = row.SeriesId ?? seriesId;
            }
            else
            {
                occupant.SeriesId ??= row.SeriesId ?? seriesId;
            }

            occupant.Removed = false;
            row.Removed = true;
            retired.Add(row);
        }

        if (retired.Count == 0) return;
        var retiredIds = retired.Select(r => r.Id).ToList();
        foreach (var finding in retiredIds.SelectMany(id => openFindings[id])) finding.State = "resolved";

        // The retired rows are still live in the database until the caller saves, so the twin check excludes them by id.
        var hashes = retired.Where(r => r.ContentHash != null).Select(r => r.ContentHash!).Except(carriedHashes).Distinct().ToList();
        if (hashes.Count == 0) return;
        var lonely = await db.HealthFindings
            .Where(x => x.Kind == "duplicate" && x.State != "resolved")
            .Where(x => db.HealthFiles.Any(f => f.Id == x.FileId && !f.Removed && !retiredIds.Contains(f.Id)
                && f.ContentHash != null && hashes.Contains(f.ContentHash)
                && !db.HealthFiles.Any(o => o.Id != f.Id && !o.Removed && !retiredIds.Contains(o.Id) && o.ContentHash == f.ContentHash)))
            .ToListAsync(ct);
        touched.AddRange(lonely);
        foreach (var finding in lonely) finding.State = "resolved";
    }

    /// <summary>
    /// Marks the live rows removed, resolves their findings and any <c>duplicate</c> whose last twin
    /// just left. Writes straight to the database; the caller owns any transaction.
    /// </summary>
    public static async Task<List<int>> RetireAsync(MakiDbContext db, IQueryable<HealthFile> rows, CancellationToken ct)
    {
        var live = await rows.Where(f => !f.Removed).Select(f => new { f.Id, f.ContentHash }).ToListAsync(ct);
        if (live.Count == 0) return [];
        var ids = live.Select(f => f.Id).ToList();
        var hashes = live.Where(f => f.ContentHash != null).Select(f => f.ContentHash!).Distinct().ToList();
        await db.HealthFiles.Where(f => ids.Contains(f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true), ct);
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

    private static void TakeAnalysis(HealthFile to, HealthFile from, string analysisJson)
    {
        to.Version = Guid.NewGuid().ToString("N");
        to.Size = from.Size;
        to.ModifiedAt = from.ModifiedAt;
        to.ContentHash = from.ContentHash;
        to.AnalyzerVersion = from.AnalyzerVersion;
        to.VerifiedVersion = from.VerifiedVersion;
        to.Status = from.Status;
        to.AnalysisJson = analysisJson;
        to.AnalyzedAt = from.AnalyzedAt;
    }

    // Everything but AnalysisJson, which is tens of KB of page fingerprints per row and only read for a collision.
    private static async Task<List<HealthFile>> AttachAsync(MakiDbContext db, IQueryable<HealthFile> query, List<object> touched, CancellationToken ct)
    {
        var found = await query.AsNoTracking()
            .Select(f => new HealthFile
            {
                Id = f.Id, RootFolderId = f.RootFolderId, SeriesId = f.SeriesId, ChapterFileId = f.ChapterFileId,
                RelativePath = f.RelativePath, Version = f.Version, Size = f.Size, ModifiedAt = f.ModifiedAt,
                ContentHash = f.ContentHash, AnalyzerVersion = f.AnalyzerVersion, VerifiedVersion = f.VerifiedVersion,
                Status = f.Status, AnalyzedAt = f.AnalyzedAt, Removed = f.Removed,
            })
            .ToListAsync(ct);
        for (var i = 0; i < found.Count; i++)
        {
            if (db.HealthFiles.Local.FindEntry(found[i].Id) is { } entry) found[i] = entry.Entity;
            else db.HealthFiles.Attach(found[i]);
            touched.Add(found[i]);
        }

        return found;
    }
}
