using System.Text.Json;
using System.Security.Cryptography;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public class HealthScanService(MakiDbContext db)
{
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> Running = new();
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>One page repeated this many times is a finding whatever the archive's length.</summary>
    private const int RepeatedPageRun = 5;
    /// <summary>
    /// The stored analysis, with every list guaranteed present. Rows written by an older analyzer
    /// deserialize with nulls where its shape differed (v1 stored pairwise repetitions and no page
    /// groups), and they stay readable until the next scan rewrites them.
    /// </summary>
    public static ArchiveAnalysis Analysis(HealthFile file)
    {
        var stored = JsonSerializer.Deserialize<ArchiveAnalysis>(file.AnalysisJson, Json);
        return new ArchiveAnalysis(stored?.Status ?? "pending", stored?.Hash,
            stored?.Pages ?? [], stored?.Problems ?? [], stored?.Groups ?? []);
    }

    public async Task RunAsync(HealthScan scan, CancellationToken ct, int workers = 0)
    {
        scan.Status = "running";
        scan.Completed = 0;
        scan.Error = null;
        await db.SaveChangesAsync(ct);
        var selected = JsonSerializer.Deserialize<int[]>(scan.FileIdsJson) ?? [];
        var roots = await db.RootFolders.OrderBy(r => r.Id).ToListAsync(ct);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var trackedLocations = await db.ChapterFiles.Join(db.Series, f => f.SeriesId, s => s.Id, (f, s) => new { s.RootFolderId, f.RelativePath }).ToListAsync(ct);
        var files = new List<HealthFile>();
        foreach (var root in roots)
        {
            if (scan.RootFolderId != null && root.Id != scan.RootFolderId) continue;
            try
            {
                HealthPaths.Resolve(root.Path, ".maki-health-check");
                if (!Directory.Exists(root.Path)) throw new IOException("Root folder unavailable");
                var tracked = await db.ChapterFiles.Where(f => db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == root.Id)).ToListAsync(ct);
                var knownFiles = await db.HealthFiles.Where(f => f.RootFolderId == root.Id).ToListAsync(ct);
                var paths = new HashSet<string>(tracked.Select(f => f.RelativePath), seen.Comparer);
                foreach (var known in knownFiles.Where(f => !f.Removed)) paths.Add(known.RelativePath);
                // Series and selected-file scans do not enumerate unrelated unlinked archives.
                if (scan.SeriesId == null && selected.Length == 0)
                    foreach (var path in HealthPaths.Archives(root.Path)) paths.Add(Path.GetRelativePath(root.Path, path));
                foreach (var relative in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    var absolute = HealthPaths.Resolve(root.Path, relative);
                    var trackedFile = tracked.FirstOrDefault(f => seen.Comparer.Equals(f.RelativePath, relative));
                    if (trackedFile == null && trackedLocations.Any(t => t.RootFolderId != root.Id && roots.Any(r => r.Id == t.RootFolderId && seen.Comparer.Equals(Path.GetFullPath(Path.Combine(r.Path, t.RelativePath)), absolute)))) continue;
                    if (!seen.Add(absolute)) continue;
                    if (scan.SeriesId != null && trackedFile?.SeriesId != scan.SeriesId) continue;
                    var file = knownFiles.FirstOrDefault(f => seen.Comparer.Equals(f.RelativePath, relative));
                    if (selected.Length > 0 && (file == null || !selected.Contains(file.Id))) continue;
                    if (file == null)
                    {
                        file = new HealthFile { RootFolderId = root.Id, RelativePath = relative };
                        db.HealthFiles.Add(file);
                    }
                    var linkageChanged = file.ChapterFileId != trackedFile?.Id;
                    file.ChapterFileId = trackedFile?.Id;
                    file.SeriesId = trackedFile?.SeriesId;
                    if (linkageChanged) file.Status = "pending";
                    file.Removed = false;
                    files.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                scan.Error = $"{scan.Error} Root {root.Id}: {ex.Message}".Trim();
            }
        }
        // Explicit selections include unlinked inventory records even when disk enumeration is skipped.
        foreach (var file in await db.HealthFiles.Where(f => selected.Contains(f.Id) && !f.Removed).ToListAsync(ct))
            if (!files.Contains(file)) files.Add(file);
        scan.Total = files.Count;
        await db.SaveChangesAsync(ct);

        // From here the scan works by id and lets every entity go after each file. Keeping them
        // tracked is what a scan naturally does and it does not survive a real library: each
        // analysis is tens of KB of page fingerprints, held twice over by EF's original-value
        // snapshot, and DetectChanges re-walks the whole set on every save. Measured over 4000
        // files it ran 2.5x slower and its memory climbed for the entire run instead of holding
        // flat.
        var pending = files.Select(f => f.Id).ToList();
        var rootPaths = roots.ToDictionary(r => r.Id, r => r.Path);
        var force = scan.Force;
        var scanId = scan.Id;
        db.ChangeTracker.Clear();

        HealthScan? current = null;
        foreach (var id in pending)
        {
            current = await db.HealthScans.FindAsync([scanId], ct);
            if (current == null || current.Status == "cancelled") return;
            var file = await db.HealthFiles.FindAsync([id], ct);
            if (file != null)
            {
                try
                {
                    if (!await db.DownloadQueue.AnyAsync(q => q.SeriesId == file.SeriesId && q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled, ct) &&
                        !await db.HealthOperations.AnyAsync(o => o.FileId == file.Id && o.Status != "completed" && o.Status != "cancelled" && o.Status != "failed", ct))
                        await AnalyzeAsync(file, rootPaths[file.RootFolderId], force, ct, workers);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    current.Error = $"{current.Error} File {file.Id}: {ex.Message}".Trim();
                }
            }
            current.Completed++;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        current = await db.HealthScans.FindAsync([scanId], ct);
        if (current == null) return;
        current.Status = current.Error == null ? "completed" : "partial";
        current.FinishedAt = DateTime.UtcNow;
        db.HealthHistory.Add(new() { Kind = "scan", Message = $"Scan {scanId}: {current.Completed}/{current.Total} files, {current.Status}" });
        await db.SaveChangesAsync(ct);
        // The caller was handed a HealthScan and reads it after this returns; it detached with the
        // first Clear, so hand back what was actually written.
        scan.Status = current.Status;
        scan.Completed = current.Completed;
        scan.Error = current.Error;
        scan.FinishedAt = current.FinishedAt;
        // ImageSharp pools the buffers it decoded into and holds them for the life of the process.
        // Measured across a real library that is about 140 MB still held once a scan is over, for a
        // job that next runs tomorrow morning.
        SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
    }

    public async Task AnalyzeAsync(HealthFile file, string root, bool force, CancellationToken ct, int workers = 0)
    {
        var path = HealthPaths.Resolve(root, file.RelativePath);
        var before = new FileInfo(path);
        var size = before.Exists ? before.Length : -1;
        var modified = before.Exists ? before.LastWriteTimeUtc : DateTime.MinValue;
        if (!force && file.AnalyzerVersion == ArchiveHealthAnalyzer.Version && file.Status == "complete" && file.Size == size && file.ModifiedAt == modified) return;
        ArchiveAnalysis? analysis = null;
        string? hash = null;
        if (before.Exists)
        {
            await using var stream = File.OpenRead(path);
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            var cached = await db.HealthAnalyses.FindAsync([$"{hash}:{ArchiveHealthAnalyzer.Version}"], ct);
            if (cached != null) analysis = JsonSerializer.Deserialize<ArchiveAnalysis>(cached.AnalysisJson, Json);
        }
        // Hand the hash on: checking the cache already read the whole archive, and the analyzer
        // would otherwise read and hash every file in the library a second time.
        analysis ??= await ArchiveHealthAnalyzer.AnalyzeAsync(path, ct, workers, hash);
        var after = new FileInfo(path);
        if (size != (after.Exists ? after.Length : -1) || modified != (after.Exists ? after.LastWriteTimeUtc : DateTime.MinValue))
        {
            file.Status = "pending";
            return;
        }
        var changed = file.ContentHash != analysis.Hash || file.Size != size || file.AnalyzerVersion != ArchiveHealthAnalyzer.Version;
        if (changed) file.Version = Guid.NewGuid().ToString("N");
        file.Size = size;
        file.ModifiedAt = modified;
        file.ContentHash = analysis.Hash;
        file.Status = analysis.Status;
        file.AnalyzerVersion = ArchiveHealthAnalyzer.Version;
        file.AnalyzedAt = DateTime.UtcNow;
        file.AnalysisJson = JsonSerializer.Serialize(analysis, Json);
        if (!await db.HealthFileVersions.AnyAsync(v => v.Id == file.Version, ct))
            db.HealthFileVersions.Add(new() { Id = file.Version, FileId = file.Id, ContentHash = file.ContentHash, Size = file.Size, ModifiedAt = file.ModifiedAt, RelativePath = file.RelativePath, AnalyzerVersion = file.AnalyzerVersion });
        if (analysis.Hash != null && analysis.Status == "complete" && !await db.HealthAnalyses.AnyAsync(a => a.Id == $"{analysis.Hash}:{ArchiveHealthAnalyzer.Version}", ct))
            db.HealthAnalyses.Add(new() { Id = $"{analysis.Hash}:{ArchiveHealthAnalyzer.Version}", ContentHash = analysis.Hash, AnalyzerVersion = ArchiveHealthAnalyzer.Version, AnalysisJson = file.AnalysisJson });
        var problems = analysis.Problems.ToList();
        // What this is for: Maki fetches pages one at a time and nothing in the download path
        // compares them, so a source serving one image for several page URLs - or a CDN handing
        // back a placeholder for the pages that failed - produces an archive with the right page
        // count, a valid CRC and pages that all decode. This is the only check that would notice.
        //
        // Which is why it is a proportion and not a presence. A reused spread is two copies in two
        // hundred pages and means nothing; a failed download is most of the archive being one
        // image. Reported as a finding only when the repetition is large enough to be the second
        // thing. Blanks never raise one at all: a chapter break, a credits filler and a failed
        // page are the same picture, so the count is shown on the file as evidence and left there.
        var copies = analysis.Groups.Where(g => g.Kind != "blank").Sum(g => g.Pages.Count - 1);
        var largest = analysis.Groups.Where(g => g.Kind != "blank").Select(g => g.Pages.Count).DefaultIfEmpty(0).Max();
        if (analysis.Pages.Count > 0 && (largest >= RepeatedPageRun || copies * 4 >= analysis.Pages.Count))
            problems.Add(new("pageRepetition", "warning",
                $"{copies} of {analysis.Pages.Count} pages repeat content already in this archive; a failed download looks like this"));
        if (file.ChapterFileId == null) problems.Add(new("unlinked", "warning", "Archive is not linked to any chapter"));
        else if (await db.ChapterFiles.AnyAsync(f => f.Id == file.ChapterFileId && f.Size != size, ct))
            problems.Add(new("sizeMismatch", "warning", "Stored size differs from the file on disk"));
        if (analysis.Hash != null)
        {
            var duplicates = await db.HealthFiles.Where(f => f.Id != file.Id && !f.Removed && f.ContentHash == analysis.Hash).ToListAsync(ct);
            if (duplicates.Count > 0)
            {
                problems.Add(new("duplicate", "warning", "Byte-identical archives exist in the library"));
                foreach (var other in duplicates)
                    if (!await db.HealthFindings.AnyAsync(f => f.FileId == other.Id && f.Version == other.Version && f.Kind == "duplicate", ct))
                        db.HealthFindings.Add(new() { FileId = other.Id, Version = other.Version, Kind = "duplicate", Message = "Byte-identical archives exist in the library", CreatedAt = DateTime.UtcNow });
            }
        }
        var existing = await db.HealthFindings.Where(f => f.FileId == file.Id).ToListAsync(ct);
        foreach (var old in existing.Where(x => x.Version != file.Version || (analysis.Status == "complete" && !problems.Any(p => p.Kind == x.Kind)))) old.State = "resolved";
        foreach (var group in problems.GroupBy(p => p.Kind))
        {
            var first = group.First();
            var finding = existing.FirstOrDefault(x => x.Version == file.Version && x.Kind == first.Kind);
            if (finding == null)
                db.HealthFindings.Add(new() { FileId = file.Id, Version = file.Version, Kind = first.Kind, Severity = first.Severity, Message = string.Join("; ", group.Select(p => p.Message).Take(5)), CreatedAt = DateTime.UtcNow });
            else if (finding.State == "resolved") finding.State = "open";
        }
        await db.SaveChangesAsync(ct);
    }
}
