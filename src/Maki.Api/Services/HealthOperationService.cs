using System.Security.Cryptography;
using System.Text.Json;
using Maki.Api.Hubs;
using Maki.Api.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="SourceMappingId">Which mapping actually produced this candidate. Recorded per
/// chapter because an automatic request resolves each chapter independently and they can land on
/// different sources; null on journals written before automatic requests existed.</param>
public record RepairCandidate(int ChapterId, string RelativePath, string Hash, ArchiveAnalysis Analysis,
    string? FinalPath = null, int? SourceMappingId = null);
public class HealthOperationService(MakiDbContext db, DownloadQueueService queue,
    ChapterSourceResolver resolver, ReaderArchiveCache archives, EventBroadcaster events, KavitaScanService kavita, AppPaths? paths = null)
{
    public static readonly SemaphoreSlim MutationGate = new(1);
    public static bool Terminal(string status) => status is "completed" or "failed" or "cancelled";
    public static List<RepairCandidate> Candidates(HealthOperation op) => JsonSerializer.Deserialize<List<RepairCandidate>>(op.JournalJson, HealthScanService.Json) ?? [];

    public async Task<(HealthFile File, RootFolder Root, List<Chapter> Chapters)> ValidateAsync(int id, string version, CancellationToken ct, int? operationId = null)
    {
        var file = await db.HealthFiles.FindAsync([id], ct) ?? throw new InvalidOperationException("File no longer exists in inventory");
        if (file.Removed || file.Version != version || file.Status == "pending") throw new InvalidOperationException("File review is stale; rescan and review again");
        var root = await db.RootFolders.FindAsync([file.RootFolderId], ct) ?? throw new InvalidOperationException("Root folder no longer exists");
        var path = HealthPaths.Resolve(root.Path, file.RelativePath);
        if (!Directory.Exists(root.Path)) throw new InvalidOperationException("Root folder is unavailable");
        var info = new FileInfo(path);
        if ((info.Exists ? info.Length : -1) != file.Size || (info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue) != file.ModifiedAt)
            throw new InvalidOperationException("File changed after review; rescan first");
        if (info.Exists)
        {
            await using var stream = File.OpenRead(path);
            if (file.ContentHash == null || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != file.ContentHash)
                throw new InvalidOperationException("File content changed or was not hashed; rescan first");
        }
        var chapterFiles = await db.ChapterFiles.Where(f => db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == root.Id) && f.RelativePath == file.RelativePath).Select(f => f.Id).ToListAsync(ct);
        var chapters = await db.Chapters.Where(c => c.ChapterFileId != null && chapterFiles.Contains(c.ChapterFileId.Value)).ToListAsync(ct);
        if (await db.HealthOperations.AnyAsync(o => o.FileId == id && o.Id != operationId && o.Status != "completed" && o.Status != "failed" && o.Status != "cancelled", ct))
            throw new InvalidOperationException("Another file operation is active");
        var seriesIds = chapters.Select(c => c.SeriesId).ToArray();
        if (await db.DownloadQueue.AnyAsync(q => seriesIds.Contains(q.SeriesId) && q.HealthOperationId != operationId && q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled && q.Status != QueueStatus.Failed, ct))
            throw new InvalidOperationException("Downloads or imports for this series are active");
        return (file, root, chapters);
    }

    /// <param name="mappingId">
    /// The source to take the replacement from, or null to let the series' own priority order
    /// decide, the same way an ordinary download does.
    /// <para>
    /// A named mapping excludes every other one: the reviewer picked that source, and quietly
    /// falling back to another would hand them a candidate from somewhere they did not choose.
    /// Automatic makes no such promise, so it keeps the fallback - which is the point of it, since
    /// the highest-priority source does not always carry the chapter.
    /// </para>
    /// </param>
    public async Task<HealthOperation> RequestAsync(int fileId, string version, int? mappingId, int userId, CancellationToken ct)
    {
        await MutationGate.WaitAsync(ct);
        try
        {
            var (file, _, chapters) = await ValidateAsync(fileId, version, ct);
            if (chapters.Count == 0 || chapters.Select(c => c.SeriesId).Distinct().Count() != 1)
                throw new InvalidOperationException("Import and link this archive to one series before replacement");
            if (mappingId != null && !await db.SourceMappings.AnyAsync(m => m.Id == mappingId && m.Enabled && m.SeriesId == chapters[0].SeriesId, ct))
                throw new InvalidOperationException("Select an enabled source mapped to this series");
            if (mappingId == null && !await db.SourceMappings.AnyAsync(m => m.Enabled && m.SeriesId == chapters[0].SeriesId, ct))
                throw new InvalidOperationException("This series has no enabled source mappings");
            var excludes = mappingId == null
                ? null
                : await db.SourceMappings.Where(m => m.SeriesId == chapters[0].SeriesId && m.Id != mappingId).Select(m => m.Id).ToListAsync(ct);
            var resolved = new List<(Chapter Chapter, ResolvedChapterSource Source)>();
            foreach (var chapter in chapters)
                resolved.Add((chapter, await resolver.ResolveAsync(db, chapter, mappingId, ct, excludes, requireExactMatch: true)));
            var op = new HealthOperation { FileId = file.Id, Version = version, SourceMappingId = mappingId, UserId = userId, Status = "downloading" };
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.HealthOperations.Add(op);
            await db.SaveChangesAsync(ct);
            foreach (var (chapter, source) in resolved)
                await queue.EnqueueRepairAsync(db, op.Id, chapter, source, userId, ct);
            await transaction.CommitAsync(ct);
            db.HealthHistory.Add(new() { Kind = "repair", FileId = fileId, UserId = userId, Message = $"Replacement requested for {chapters.Count} chapters" });
            await db.SaveChangesAsync(ct);
            return op;
        }
        finally { MutationGate.Release(); }
    }

    public async Task<HealthOperation> PreviewDeleteAsync(int fileId, string version, int userId, CancellationToken ct)
    {
        await MutationGate.WaitAsync(ct);
        try
        {
            await ValidateAsync(fileId, version, ct);
            var op = new HealthOperation { Kind = "delete", Status = "review", FileId = fileId, Version = version, UserId = userId };
            db.HealthOperations.Add(op);
            await db.SaveChangesAsync(ct);
            return op;
        }
        finally { MutationGate.Release(); }
    }

    public async Task ApplyAsync(int operationId, string version, bool confirmed, bool resetPositions, CancellationToken ct)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit confirmation is required");
        await MutationGate.WaitAsync(ct);
        try
        {
            var op = await db.HealthOperations.FindAsync([operationId], ct) ?? throw new InvalidOperationException("Operation not found");
            if (op.Status != "review" || op.Version != version) throw new InvalidOperationException("Operation is not ready or review is stale");
            var (file, root, chapters) = await ValidateAsync(op.FileId, version, ct, op.Id);
            if (op.Kind == "delete")
            {
                op.Status = "deleting";
                await db.SaveChangesAsync(ct);
                // An archive the inventory recorded as missing has nothing to delete, so the
                // operation is only the record cleanup. Validation has already confirmed it is
                // still absent - a file that came back fails the size check and never reaches
                // here - and attempting the delete anyway would fail the whole operation when the
                // series folder went with it, leaving chapters pointing at a file that is gone.
                if (file.Size >= 0)
                {
                    try { File.Delete(HealthPaths.Resolve(root.Path, file.RelativePath)); }
                    catch
                    {
                        op.Status = "failed"; op.Error = "File deletion failed; file links retained";
                        await db.SaveChangesAsync(CancellationToken.None);
                        throw;
                    }
                }
                await CompleteDeletionAsync(op, file, root, ct);
                return;
            }
            var original = HealthPaths.Resolve(root.Path, file.RelativePath);
            var candidates = Candidates(op);
            if (op.Kind == "repair")
            {
                if (chapters.Count == 0 || candidates.Count != chapters.Count || !chapters.All(c => candidates.Count(p => p.ChapterId == c.Id) == 1))
                    throw new InvalidOperationException("Replacement must cover every linked chapter");
                foreach (var candidate in candidates)
                {
                    var path = HealthPaths.Resolve(root.Path, candidate.RelativePath);
                    await using var stream = File.OpenRead(path);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != candidate.Hash)
                        throw new InvalidOperationException("Candidate changed; request a new replacement");
                    if (candidate.Analysis.Status != "complete" || candidate.Analysis.Problems.Any(p => p.Severity == "error"))
                        throw new InvalidOperationException("Candidate failed validation or analysis is incomplete");
                }

            }
            var reset = op.Kind == "repair" && RequiresReset(file, candidates, chapters.Count);
            if (reset && !resetPositions) throw new InvalidOperationException("Confirm resetting affected bookmarks and resume positions");
            var rollbackRelative = $".maki/health/{op.Id}/original.cbz";
            var rollback = HealthPaths.Resolve(root.Path, rollbackRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
            candidates = candidates.Select(c => c with { FinalPath = Path.Combine(Path.GetDirectoryName(file.RelativePath) ?? "", $"health-{op.Id}-chapter-{c.ChapterId}.cbz") }).ToList();
            foreach (var candidate in candidates)
                if (File.Exists(HealthPaths.Resolve(root.Path, candidate.FinalPath!))) throw new InvalidOperationException("Replacement destination already exists");
            op.JournalJson = JsonSerializer.Serialize(candidates, HealthScanService.Json);
            op.Status = "applying";
            await db.SaveChangesAsync(ct);
            try
            {
                // The rollback name is an operation journal, not user-visible quarantine.
                if (File.Exists(original)) File.Move(original, rollback);
                foreach (var candidate in candidates)
                    File.Move(HealthPaths.Resolve(root.Path, candidate.RelativePath), HealthPaths.Resolve(root.Path, candidate.FinalPath!));
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                foreach (var chapter in chapters)
                {
                    chapter.ChapterFileId = null;
                    if (op.Kind == "repair")
                    {
                        var candidate = candidates.Single(c => c.ChapterId == chapter.Id);
                        // The candidate's own mapping first: an automatic request has none on the
                        // operation, and its chapters need not share one.
                        var mappingId = candidate.SourceMappingId ?? op.SourceMappingId;
                        var mapping = mappingId == null ? null : await db.SourceMappings.FindAsync([mappingId], ct);
                        var replacement = new ChapterFile { SeriesId = chapter.SeriesId, RelativePath = candidate.FinalPath!, Size = new FileInfo(HealthPaths.Resolve(root.Path, candidate.FinalPath!)).Length, SourceName = mapping?.SourceName ?? "health", DateAdded = DateTime.UtcNow };
                        db.ChapterFiles.Add(replacement);
                        await db.SaveChangesAsync(ct);
                        chapter.ChapterFileId = replacement.Id;
                        if (reset)
                        {
                            db.ReaderBookmarks.RemoveRange(await db.ReaderBookmarks.IgnoreQueryFilters().Where(b => b.ChapterId == chapter.Id).ToListAsync(ct));
                            foreach (var progress in await db.ChapterProgress.IgnoreQueryFilters().Where(p => p.ChapterId == chapter.Id).ToListAsync(ct))
                            { progress.PageIndex = 0; progress.PageCount = candidate.Analysis.Pages.Count; }
                        }
                    }
                }
                // Include stale file rows even if no chapter still references them.
                var oldFiles = await db.ChapterFiles.Where(f => f.RelativePath == file.RelativePath && db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == root.Id)).ToListAsync(ct);
                db.ChapterFiles.RemoveRange(oldFiles);
                foreach (var old in oldFiles) Invalidate(old.Id);
                file.Removed = true;
                foreach (var finding in await db.HealthFindings.Where(f => f.FileId == file.Id).ToListAsync(ct)) finding.State = "resolved";
                op.Status = "completed"; op.FinishedAt = DateTime.UtcNow;
                db.HealthHistory.Add(new() { Kind = op.Kind, FileId = file.Id, UserId = op.UserId, Message = $"{op.Kind} completed: {file.RelativePath}" });
                db.HealthScans.Add(new() { RootFolderId = root.Id });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                db.ChangeTracker.Clear();
                await RecoverAsync(CancellationToken.None);
                throw;
            }
            // A failed cleanup is retried on startup; the committed replacement remains authoritative.
            try { if (File.Exists(rollback)) File.Delete(rollback); } catch (IOException) { }
            foreach (var chapter in chapters)
                await events.ChapterImported(chapter.SeriesId, chapter.Id, root.Id);
            if (file.SeriesId is {} seriesId) kavita.QueueScan(Path.GetDirectoryName(original)!, seriesId);
        }
        finally { MutationGate.Release(); }
    }

    public static bool RequiresReset(HealthFile file, List<RepairCandidate> candidates, int chapters)
    {
        if (chapters != 1 || candidates.Count != 1) return true;
        var old = HealthScanService.Analysis(file).Pages;
        var next = candidates[0].Analysis.Pages;
        return old.Count != next.Count || old.Count == 0 || old.Zip(next).Any(p => p.First.RawHash != p.Second.RawHash && (p.First.PixelHash == null || p.First.PixelHash != p.Second.PixelHash));
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var op in await db.HealthOperations.Where(o => o.Status == "deleting").ToListAsync(ct))
        {
            var file = await db.HealthFiles.FindAsync([op.FileId], ct) ?? throw new IOException("Missing deletion journal file");
            var root = await db.RootFolders.FindAsync([file.RootFolderId], ct) ?? throw new IOException("Missing deletion journal root");
            if (!Directory.Exists(root.Path)) throw new IOException("Cannot recover deletion while root is unavailable");
            if (File.Exists(HealthPaths.Resolve(root.Path, file.RelativePath)))
            { op.Status = "failed"; op.Error = "Deletion was interrupted before file removal; links retained"; await db.SaveChangesAsync(ct); }
            else await CompleteDeletionAsync(op, file, root, ct);
        }
        foreach (var op in await db.HealthOperations.Where(o => o.Status == "applying" || o.Status == "completed").ToListAsync(ct))
        {
            var file = await db.HealthFiles.FindAsync([op.FileId], ct);
            var root = file == null ? null : await db.RootFolders.FindAsync([file.RootFolderId], ct);
            if (file == null || root == null) continue;
            var original = HealthPaths.Resolve(root.Path, file.RelativePath);
            var rollback = HealthPaths.Resolve(root.Path, $".maki/health/{op.Id}/original.cbz");
            if (op.Status == "completed") { if (File.Exists(rollback)) File.Delete(rollback); continue; }
            if (!Directory.Exists(root.Path)) throw new IOException("Cannot recover health operation while root is unavailable");
            foreach (var candidate in Candidates(op))
            {
                if (candidate.FinalPath == null) continue;
                var final = HealthPaths.Resolve(root.Path, candidate.FinalPath);
                var stage = HealthPaths.Resolve(root.Path, candidate.RelativePath);
                if (File.Exists(final))
                {
                    await using (var stream = File.OpenRead(final))
                        if (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != candidate.Hash)
                            throw new IOException("Recovery candidate changed; refusing to move unrelated content");
                    if (File.Exists(stage)) throw new IOException("Recovery found conflicting candidate files");
                    File.Move(final, stage);
                }
            }
            if (File.Exists(rollback))
            {
                await using (var stream = File.OpenRead(rollback))
                    if (file.ContentHash != null && Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != file.ContentHash)
                        throw new IOException("Recovery original changed; manual inspection required");
                if (File.Exists(original)) throw new IOException("Recovery found conflicting original files");
                File.Move(rollback, original);
            }
            op.Status = "failed"; op.Error = "Interrupted application rolled back; rescan before retrying";
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task CompleteDeletionAsync(HealthOperation op, HealthFile file, RootFolder root, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var oldFiles = await db.ChapterFiles.Where(f => f.RelativePath == file.RelativePath && db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == root.Id)).ToListAsync(ct);
        var ids = oldFiles.Select(f => f.Id).ToArray();
        foreach (var chapter in await db.Chapters.Where(c => c.ChapterFileId != null && ids.Contains(c.ChapterFileId.Value)).ToListAsync(ct)) chapter.ChapterFileId = null;
        db.ChapterFiles.RemoveRange(oldFiles);
        foreach (var old in oldFiles) Invalidate(old.Id);
        file.Removed = true;
        foreach (var finding in await db.HealthFindings.Where(f => f.FileId == file.Id).ToListAsync(ct)) finding.State = "resolved";
        op.Status = "completed"; op.FinishedAt = DateTime.UtcNow;
        db.HealthHistory.Add(new()
        {
            Kind = "delete", FileId = file.Id, UserId = op.UserId,
            Message = file.Size < 0
                ? $"Cleared the record for missing {file.RelativePath}; chapter records and Wanted flags preserved"
                : $"Permanently deleted {file.RelativePath}; chapter records and Wanted flags preserved"
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private void Invalidate(int id)
    {
        archives.Invalidate(id);
        if (paths == null) return;
        var directory = Path.Combine(paths.ReaderCacheDir, id.ToString());
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
