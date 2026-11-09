using System.Text.Json;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Where a missing file most likely went: an unlinked archive in the same root with the same file
/// name, which is what a folder renamed or reorganised outside Maki leaves behind. Read-only; a
/// relink re-runs this and acts only when it still names the same file.
/// </summary>
/// <remarks>
/// The whole-file hash rarely survives the move, because the tools that move files (taggers, other
/// library managers) also rewrite ComicInfo.xml. So the evidence is graded, strongest first:
/// <list type="bullet">
/// <item><c>identical</c>: the same bytes as a version this row once had.</item>
/// <item><c>samePages</c>: every page hash matches the old verified analysis; only metadata differs.</item>
/// <item><c>likely</c>: same page count as the record and a size within a few percent of it.</item>
/// <item><c>nameOnly</c>: nothing but the name agrees. Offered one file at a time, never in bulk.</item>
/// </list>
/// </remarks>
public static class HealthMovedFiles
{
    public const string Identical = "identical";
    public const string SamePages = "samePages";
    public const string Likely = "likely";
    public const string NameOnly = "nameOnly";

    public static bool Strong(string evidence) => evidence is Identical or SamePages or Likely;

    public sealed record Found(int MissingId, int TargetId, string TargetVersion, string TargetPath, long TargetSize, int TargetPages, string Evidence);

    private static readonly StringComparer NameComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static int Rank(string evidence) => evidence switch { Identical => 0, SamePages => 1, Likely => 2, _ => 3 };

    private static string FileName(string relativePath) => Path.GetFileName(relativePath.Replace('\\', '/'));

    /// <summary>The best candidate for each missing file that has an unambiguous one.</summary>
    /// <remarks>
    /// Ambiguity is judged against every missing file in the root, not just the ones asked about, so
    /// the detail view and a relink of one file refuse a target another missing record also claims.
    /// </remarks>
    /// <param name="ambiguous">Collects the asked-about files that have candidates but no single best one.</param>
    public static async Task<Dictionary<int, Found>> FindAsync(MakiDbContext db, IReadOnlyCollection<int> missingIds, CancellationToken ct,
        ISet<int>? ambiguous = null)
    {
        var asking = await db.HealthFiles.AsNoTracking()
            .Where(f => missingIds.Contains(f.Id) && !f.Removed && f.Size < 0 && f.ChapterFileId != null)
            .Select(f => new { f.RootFolderId, f.RelativePath }).ToListAsync(ct);
        if (asking.Count == 0) return [];
        var rootIds = asking.Select(a => a.RootFolderId).Distinct().ToList();
        // Only a record with the same file name can compete for a candidate.
        var askedNames = asking.Select(a => FileName(a.RelativePath)).ToHashSet(NameComparer);
        var missing = (await db.HealthFiles.AsNoTracking()
                .Where(f => rootIds.Contains(f.RootFolderId) && !f.Removed && f.Size < 0 && f.ChapterFileId != null)
                .Select(f => new { f.Id, f.RootFolderId, f.RelativePath, ChapterFileId = f.ChapterFileId!.Value, f.ContentHash })
                .ToListAsync(ct))
            .Where(m => askedNames.Contains(FileName(m.RelativePath)))
            .ToList();
        var recordIds = missing.Select(m => m.ChapterFileId).ToList();
        var records = await db.ChapterFiles.AsNoTracking().Where(f => recordIds.Contains(f.Id))
            .Select(f => new { f.Id, f.SeriesId, f.Size, f.PageCount }).ToDictionaryAsync(f => f.Id, ct);

        var names = missing.Select(m => FileName(m.RelativePath)).ToHashSet(NameComparer);
        var candidates = (await db.HealthFiles.AsNoTracking()
                .Where(f => !f.Removed && f.Size >= 0 && f.ChapterFileId == null && f.Status != "pending" && rootIds.Contains(f.RootFolderId))
                .Select(f => new { f.Id, f.Version, f.RootFolderId, f.RelativePath, f.Size, f.ContentHash })
                .ToListAsync(ct))
            .Where(f => names.Contains(FileName(f.RelativePath)))
            .ToList();
        if (candidates.Count == 0) return [];
        var candidatesByName = candidates.ToLookup(c => FileName(c.RelativePath), NameComparer);

        // A path a chapter file already names belongs to that record, whatever its inventory row says.
        var claimed = (await db.ChapterFiles.AsNoTracking()
                .Join(db.Series, f => f.SeriesId, s => s.Id, (f, s) => new { s.RootFolderId, f.RelativePath })
                .Where(x => rootIds.Contains(x.RootFolderId))
                .ToListAsync(ct))
            .Select(x => (x.RootFolderId, LibraryPaths.ComparisonKey(x.RelativePath)))
            .ToHashSet();
        var series = await db.Series.AsNoTracking().Where(s => rootIds.Contains(s.RootFolderId))
            .Select(s => new { s.Id, s.RootFolderId, s.FolderName }).ToListAsync(ct);
        int? OwnerOf(int rootId, string path) => series
            .Where(s => s.RootFolderId == rootId && HealthMatchService.Contains(s.FolderName, path))
            .OrderByDescending(s => s.FolderName.Length).Select(s => (int?)s.Id).FirstOrDefault();

        var candidateIds = candidates.Select(c => c.Id).ToList();
        var candidatePages = (await db.HealthFiles.AsNoTracking().Where(f => candidateIds.Contains(f.Id))
                .Select(f => new { f.Id, f.AnalysisJson }).ToListAsync(ct))
            .ToDictionary(f => f.Id, f => Pages(f.AnalysisJson));
        var ids = missing.Select(m => m.Id).ToList();
        var oldHashes = (await db.HealthFileVersions.AsNoTracking()
                .Where(v => ids.Contains(v.FileId) && v.ContentHash != null)
                .Select(v => new { v.FileId, v.ContentHash }).ToListAsync(ct))
            .Concat(missing.Where(m => m.ContentHash != null).Select(m => new { FileId = m.Id, m.ContentHash }))
            .ToLookup(v => v.FileId, v => v.ContentHash!);
        var allOld = oldHashes.SelectMany(g => g).Distinct().ToList();
        var oldPages = (await db.HealthAnalyses.AsNoTracking().Where(a => allOld.Contains(a.ContentHash))
                .Select(a => new { a.ContentHash, a.AnalysisJson }).ToListAsync(ct))
            .GroupBy(a => a.ContentHash).ToDictionary(g => g.Key, g => Pages(g.First().AnalysisJson));

        var best = new Dictionary<int, Found>();
        var unsettled = new HashSet<int>();
        foreach (var m in missing)
        {
            if (!records.TryGetValue(m.ChapterFileId, out var record)) continue;
            var name = FileName(m.RelativePath);
            var options = new List<Found>();
            foreach (var c in candidatesByName[name].Where(c => c.RootFolderId == m.RootFolderId))
            {
                if (claimed.Contains((c.RootFolderId, LibraryPaths.ComparisonKey(c.RelativePath)))) continue;
                // Inside another series' folder it belongs to that series, however alike the names.
                if (OwnerOf(c.RootFolderId, c.RelativePath) is { } owner && owner != record.SeriesId) continue;
                var pages = candidatePages.GetValueOrDefault(c.Id) ?? [];
                var hashes = oldHashes[m.Id].ToList();
                var evidence =
                    c.ContentHash != null && hashes.Contains(c.ContentHash) ? Identical
                    : pages.Count > 0 && pages.All(p => p.RawHash != null) && hashes.Any(h =>
                        oldPages.GetValueOrDefault(h) is { Count: > 0 } old && old.Select(p => p.RawHash).SequenceEqual(pages.Select(p => p.RawHash))) ? SamePages
                    : pages.Count > 0 && record.Size > 0 && (record.PageCount == null || record.PageCount == pages.Count)
                        && Math.Abs(c.Size - record.Size) <= Math.Max(64 * 1024, record.Size / 50) ? Likely
                    : NameOnly;
                options.Add(new(m.Id, c.Id, c.Version, c.RelativePath, c.Size, pages.Count, evidence));
            }
            if (options.Count == 0) continue;
            var top = options.Min(o => Rank(o.Evidence));
            var leaders = options.Where(o => Rank(o.Evidence) == top).ToList();
            if (leaders.Count == 1) best[m.Id] = leaders[0];
            else unsettled.Add(m.Id);
        }
        // One file can only be the new home of one record.
        foreach (var target in best.Values.GroupBy(f => f.TargetId).Where(g => g.Count() > 1).SelectMany(g => g).ToList())
        {
            unsettled.Add(target.MissingId);
            best.Remove(target.MissingId);
        }
        var asked = missingIds as ISet<int> ?? missingIds.ToHashSet();
        if (ambiguous != null) foreach (var id in unsettled.Where(asked.Contains)) ambiguous.Add(id);
        return best.Where(b => asked.Contains(b.Key)).ToDictionary();
    }

    private static List<PageFingerprint> Pages(string analysisJson)
    {
        try { return JsonSerializer.Deserialize<ArchiveAnalysis>(analysisJson, HealthScanService.Json)?.Pages ?? []; }
        catch (JsonException) { return []; }
    }
}
