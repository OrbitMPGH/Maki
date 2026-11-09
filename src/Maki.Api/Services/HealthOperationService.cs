using System.Security.Cryptography;
using System.Text.Json;
using Maki.Api.Hubs;
using Maki.Api.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Core.Io;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="SourceMappingId">Which mapping actually produced this candidate. Recorded per
/// chapter because an automatic request resolves each chapter independently and they can land on
/// different sources; null on journals written before automatic requests existed.</param>
public record RepairCandidate(int ChapterId, string RelativePath, string Hash, ArchiveAnalysis Analysis,
    string? FinalPath = null, int? SourceMappingId = null);

/// <summary>
/// A PDF was placed as it is and read in place; there is nothing to rebuild it from, so it never
/// enters the download/replace flow. Thrown from <see cref="HealthOperationService.RequestAsync"/>
/// so the check runs once instead of once in the controller and again here.
/// </summary>
public sealed class PdfRepairUnsupportedException : Exception;

/// <summary>A health action refused for a reason the caller can show. <see cref="Key"/> is the catalogue key.</summary>
public sealed class HealthRefusedException(string key) : InvalidOperationException(key)
{
    public string Key { get; } = key;
}

public class HealthOperationService(MakiDbContext db, DownloadQueueService queue,
    ChapterSourceResolver resolver, ReaderArchiveCache archives, EventBroadcaster events, KavitaScanService kavita,
    ChapterFileQualityService quality, AppPaths? paths = null)
{
    public static readonly SemaphoreSlim MutationGate = new(1);
    public static bool Terminal(string status) => status is "completed" or "failed" or "cancelled";

    /// <summary>
    /// Test seam only: invoked right after <c>File.Delete</c> in the delete branch of
    /// <see cref="ApplyAsync"/>, before the DB cleanup that must survive a cancellation from that
    /// point on. Lets a test cancel the caller's token at exactly the moment production code cannot
    /// otherwise be interrupted at. Always null outside tests.
    /// </summary>
    internal Action? TestHookAfterFileDeleted;

    /// <summary>Test seam only: how two paths are told apart. <see cref="FileIdentity.Compare"/> outside tests.</summary>
    internal Func<string, string, FileSameness> SameFile = FileIdentity.Compare;
    public static List<RepairCandidate> Candidates(HealthOperation op) => JsonSerializer.Deserialize<List<RepairCandidate>>(op.JournalJson, HealthScanService.Json) ?? [];

    /// <param name="readBytes">False only where nothing acts on the bytes yet (a deletion preview):
    /// the hash is a full read of the archive, and apply checks it again before anything happens.</param>
    public async Task<(HealthFile File, RootFolder Root, List<Chapter> Chapters)> ValidateAsync(int id, string version, CancellationToken ct,
        int? operationId = null, bool readBytes = true)
    {
        var file = await db.HealthFiles.FindAsync([id], ct) ?? throw new HealthRefusedException("error.health.fileGone");
        if (file.Removed || file.Version != version || file.Status == "pending") throw new HealthRefusedException("error.health.reviewStale");
        var root = await db.RootFolders.FindAsync([file.RootFolderId], ct) ?? throw new HealthRefusedException("error.health.rootGone");
        var path = HealthPaths.Resolve(root.Path, file.RelativePath);
        if (!Directory.Exists(root.Path)) throw new HealthRefusedException("error.health.rootUnavailable");
        var info = new FileInfo(path);
        if ((info.Exists ? info.Length : -1) != file.Size || (info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue) != file.ModifiedAt)
            throw new HealthRefusedException("error.health.fileChanged");
        if (info.Exists && file.ContentHash == null) throw new HealthRefusedException("error.health.fileContentChanged");
        if (info.Exists && readBytes && !await HashMatchesAsync(path, file.ContentHash!, ct))
            throw new HealthRefusedException("error.health.fileContentChanged");
        var chapterFiles = (await HealthInventory.RecordsAtAsync(db, root.Id, file.RelativePath, ct)).Select(f => f.Id).ToList();
        var chapters = await db.Chapters.Where(c => c.ChapterFileId != null && chapterFiles.Contains(c.ChapterFileId.Value)).ToListAsync(ct);
        if (await db.HealthOperations.AnyAsync(o => o.FileId == id && o.Id != operationId && o.Status != "completed" && o.Status != "failed" && o.Status != "cancelled", ct))
            throw new HealthRefusedException("error.health.operationActive");
        var seriesIds = chapters.Select(c => c.SeriesId).ToArray();
        if (await db.DownloadQueue.AnyAsync(q => seriesIds.Contains(q.SeriesId) && q.HealthOperationId != operationId && q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled && q.Status != QueueStatus.Failed, ct))
            throw new HealthRefusedException("error.health.downloadsActive");
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
            if (ComicFile.IsPdf(file.RelativePath)) throw new PdfRepairUnsupportedException();
            if (chapters.Count == 0 || chapters.Select(c => c.SeriesId).Distinct().Count() != 1)
                throw new HealthRefusedException("error.health.needsLinkedSeries");
            if (mappingId != null && !await db.SourceMappings.AnyAsync(m => m.Id == mappingId && m.Enabled && m.SeriesId == chapters[0].SeriesId, ct))
                throw new HealthRefusedException("error.health.mappingNotEnabled");
            if (mappingId == null && !await db.SourceMappings.AnyAsync(m => m.Enabled && m.SeriesId == chapters[0].SeriesId, ct))
                throw new HealthRefusedException("error.download.noEnabledMapping");
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
            db.HealthHistory.Add(new()
            {
                Kind = "repair",
                FileId = fileId,
                UserId = userId,
                MessageKey = "health.history.repairRequested",
                ParamsJson = JsonSerializer.Serialize(new { count = chapters.Count }),
            });
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
            var (file, _, chapters) = await ValidateAsync(fileId, version, ct, readBytes: false);
            var target = chapters.Count > 0 ? await HealthDuplicates.RelinkTargetAsync(db, file, ct, SameFile) : null;
            var op = new HealthOperation { Kind = "delete", Status = "review", FileId = fileId, Version = version, UserId = userId, RelinkFileId = target?.Id };
            db.HealthOperations.Add(op);
            await db.SaveChangesAsync(ct);
            return op;
        }
        finally { MutationGate.Release(); }
    }

    /// <summary>
    /// Preview and apply under one confirmation the caller already has. A refusal at apply fails the
    /// operation instead of leaving it in review, where it would block every later request on the file.
    /// </summary>
    /// <param name="spare">The caller deletes a spare copy: refuse unless the file still backs nothing
    /// and another copy of the same bytes is still on disk as a file of its own.</param>
    public async Task<HealthOperation> DeleteNowAsync(int fileId, string version, int userId, CancellationToken ct, bool spare = false)
    {
        var op = await PreviewDeleteAsync(fileId, version, userId, ct);
        try { await ApplyAsync(op.Id, version, true, false, ct, spare); }
        catch (Exception ex) when (op.Status == "review" && ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            op.Status = "failed";
            op.ErrorKey = ex is HealthRefusedException refused ? refused.Key : "health.operation.error.deleteFailed";
            op.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        return op;
    }

    /// <summary>
    /// Points a missing file's record at the copy <see cref="HealthMovedFiles"/> found for it. Nothing
    /// on disk changes: the record moves, so the chapters keep their file id, reads and quality.
    /// </summary>
    /// <param name="allowWeak">Accept a match on the name alone. Only a reviewer looking at the one
    /// file sets it; a bulk relink never does.</param>
    public async Task RelinkMovedAsync(int missingId, string missingVersion, int targetId, string targetVersion,
        bool allowWeak, int userId, CancellationToken ct)
    {
        await MutationGate.WaitAsync(ct);
        try
        {
            var missing = await db.HealthFiles.FindAsync([missingId], ct) ?? throw new HealthRefusedException("error.health.fileGone");
            if (missing.Removed || missing.Version != missingVersion || missing.Size >= 0 || missing.ChapterFileId == null)
                throw new HealthRefusedException("error.health.reviewStale");
            var root = await db.RootFolders.FindAsync([missing.RootFolderId], ct) ?? throw new HealthRefusedException("error.health.rootGone");
            if (!Directory.Exists(root.Path)) throw new HealthRefusedException("error.health.rootUnavailable");
            if (File.Exists(HealthPaths.Resolve(root.Path, missing.RelativePath))) throw new HealthRefusedException("error.health.fileChanged");
            var found = (await HealthMovedFiles.FindAsync(db, [missingId], ct)).GetValueOrDefault(missingId);
            if (found == null || found.TargetId != targetId || found.TargetVersion != targetVersion)
                throw new HealthRefusedException("error.health.movedTargetChanged");
            if (!allowWeak && !HealthMovedFiles.Strong(found.Evidence)) throw new HealthRefusedException("error.health.relinkWeakMatch");
            var target = await db.HealthFiles.FindAsync([targetId], ct) ?? throw new HealthRefusedException("error.health.movedTargetChanged");
            var info = new FileInfo(HealthPaths.Resolve(root.Path, target.RelativePath));
            if (!info.Exists || info.Length != target.Size || info.LastWriteTimeUtc != target.ModifiedAt)
                throw new HealthRefusedException("error.health.movedTargetChanged");
            var record = await db.ChapterFiles.FindAsync([missing.ChapterFileId], ct) ?? throw new HealthRefusedException("error.health.reviewStale");
            if (await db.HealthOperations.AnyAsync(o => (o.FileId == missingId || o.FileId == targetId) && o.Status != "completed" && o.Status != "failed" && o.Status != "cancelled", ct))
                throw new HealthRefusedException("error.health.operationActive");
            if (await db.DownloadQueue.AnyAsync(q => q.SeriesId == record.SeriesId && q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled && q.Status != QueueStatus.Failed, ct))
                throw new HealthRefusedException("error.health.downloadsActive");

            using var seriesLock = await SeriesLocks.SeriesAsync(record.SeriesId, ct);
            var oldPath = record.RelativePath;
            // Other bytes under the same row: whatever was measured or converted was about the old file.
            if (found.Evidence != HealthMovedFiles.Identical || record.Size != target.Size)
            {
                record.MeasuredAtUtc = null;
                record.ResetConversion();
            }
            record.RelativePath = target.RelativePath;
            record.Size = target.Size;
            Invalidate(record.Id);
            target.ChapterFileId = record.Id;
            target.SeriesId = record.SeriesId;
            target.Status = "pending";
            missing.Removed = true;
            foreach (var finding in await db.HealthFindings.Where(f => (f.FileId == missingId || (f.FileId == targetId && f.Kind == "unlinked")) && f.State != "resolved").ToListAsync(ct))
                finding.State = "resolved";
            db.HealthHistory.Add(new()
            {
                Kind = "relink", FileId = targetId, UserId = userId,
                MessageKey = "health.history.relinkedMoved",
                ParamsJson = JsonSerializer.Serialize(new { path = oldPath, target = target.RelativePath }),
            });
            db.HealthScans.Add(new() { FileIdsJson = JsonSerializer.Serialize(new[] { targetId }), Force = true });
            await db.SaveChangesAsync(ct);
            await FollowFolderAsync(record, oldPath, root, userId, ct);
            HealthWorker.Wake();
        }
        finally { MutationGate.Release(); }
    }

    /// <summary>
    /// Once the last record has left a series folder that no longer exists, the series moves with
    /// it, so new downloads land beside the files instead of recreating the old folder.
    /// </summary>
    internal async Task FollowFolderAsync(ChapterFile moved, string oldPath, RootFolder root, int userId, CancellationToken ct)
    {
        var series = await db.Series.FindAsync([moved.SeriesId], ct);
        var from = LibraryPaths.TopFolder(oldPath);
        var to = LibraryPaths.TopFolder(moved.RelativePath);
        if (series == null || from == null || to == null || string.Equals(from, to, StringComparison.Ordinal)) return;
        // A rename that only changed case has to tell the two spellings apart.
        var names = LibraryPaths.FolderComparer.Equals(from, to) ? StringComparer.Ordinal : LibraryPaths.FolderComparer;
        var oldFolder = HealthPaths.Resolve(root.Path, from);
        if (!names.Equals(series.FolderName, from)
            || (Directory.Exists(oldFolder) && !LibraryPaths.IsSameDirectory(oldFolder, HealthPaths.Resolve(root.Path, to)))) return;
        var others = await db.ChapterFiles.Where(f => f.SeriesId == series.Id && f.Id != moved.Id).Select(f => f.RelativePath).ToListAsync(ct);
        if (others.Any(p => LibraryPaths.TopFolder(p) is { } top && names.Equals(top, from))) return;
        using var folderNames = await SeriesLocks.FolderNamesAsync(ct);
        var taken = await db.Series.Where(s => s.Id != series.Id && s.RootFolderId == root.Id).Select(s => s.FolderName).ToListAsync(ct);
        if (taken.Any(n => LibraryPaths.FolderComparer.Equals(n, to))) return;
        series.FolderName = to;
        db.HealthHistory.Add(new()
        {
            Kind = "relink", UserId = userId,
            MessageKey = "health.history.seriesFolderFollowed",
            ParamsJson = JsonSerializer.Serialize(new { from, to }),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Brings a record's size back in line with the file on disk, for a file changed outside Maki.
    /// The same reset a rescan does when it sees new bytes under a known name; a rescan only
    /// does it for names it has not seen.
    /// </summary>
    public async Task<int> RefreshRecordsAsync(IReadOnlyCollection<int> fileIds, int userId, CancellationToken ct)
    {
        await MutationGate.WaitAsync(ct);
        try
        {
            var files = await db.HealthFiles.Where(f => fileIds.Contains(f.Id) && !f.Removed && f.ChapterFileId != null && f.Size >= 0).ToListAsync(ct);
            var roots = await db.RootFolders.ToDictionaryAsync(r => r.Id, ct);
            var updated = new List<int>();
            foreach (var file in files)
            {
                if (!roots.TryGetValue(file.RootFolderId, out var root) || !Directory.Exists(root.Path)) continue;
                var info = new FileInfo(HealthPaths.Resolve(root.Path, file.RelativePath));
                var record = await db.ChapterFiles.FindAsync([file.ChapterFileId], ct);
                if (!info.Exists || record == null || record.Size == info.Length) continue;
                record.Size = info.Length;
                record.MeasuredAtUtc = null;
                record.ResetConversion();
                Invalidate(record.Id);
                updated.Add(file.Id);
            }
            if (updated.Count > 0)
            {
                db.HealthScans.Add(new() { FileIdsJson = JsonSerializer.Serialize(updated), Force = true });
                db.HealthHistory.Add(new()
                {
                    Kind = "record", UserId = userId,
                    MessageKey = "health.history.recordsRefreshed",
                    ParamsJson = JsonSerializer.Serialize(new { count = updated.Count }),
                });
                await db.SaveChangesAsync(ct);
                HealthWorker.Wake();
            }
            return updated.Count;
        }
        finally { MutationGate.Release(); }
    }

    /// <summary>A failed operation that could run again: its file is still the version it failed on.</summary>
    public static bool Retryable(HealthOperation op, HealthFile? file) =>
        op.Status == "failed" && file is { Removed: false } && file.Status != "pending" && file.Version == op.Version;

    /// <summary>
    /// Starts the same request again and dismisses the failed one. A deletion comes back in review,
    /// so it still needs its confirmation; a replacement starts downloading.
    /// </summary>
    public async Task<HealthOperation> RetryAsync(int operationId, int userId, CancellationToken ct)
    {
        var op = await db.HealthOperations.FindAsync([operationId], ct) ?? throw new HealthRefusedException("error.health.operationNotFound");
        if (op.Status != "failed") throw new HealthRefusedException("error.health.operationNotRetryable");
        var next = op.Kind == "delete"
            ? await PreviewDeleteAsync(op.FileId, op.Version, userId, ct)
            : await RequestAsync(op.FileId, op.Version, op.SourceMappingId, userId, ct);
        op.DismissedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return next;
    }

    public async Task ApplyAsync(int operationId, string version, bool confirmed, bool resetPositions, CancellationToken ct, bool spare = false)
    {
        if (!confirmed) throw new HealthRefusedException("error.health.confirmationRequired");
        await MutationGate.WaitAsync(ct);
        try
        {
            var op = await db.HealthOperations.FindAsync([operationId], ct) ?? throw new HealthRefusedException("error.health.operationNotFound");
            if (op.Status != "review" || op.Version != version) throw new HealthRefusedException("error.health.operationNotReady");
            var (file, root, chapters) = await ValidateAsync(op.FileId, version, ct, op.Id);
            if (op.Kind == "delete")
            {
                if (spare) await RequireSpareAsync(file, root, chapters, ct);
                if (file.Size >= 0) await RefuseIfAnotherRecordReadsItAsync(file, root, ct);
                if (op.RelinkFileId is { } relinkId)
                {
                    // The chapters were promised a home. If that copy changed or found another use
                    // since the review, unlinking them instead is not what was confirmed.
                    var target = await HealthDuplicates.RelinkTargetAsync(db, file, ct, SameFile);
                    var targetPath = target == null ? null : HealthPaths.Resolve(root.Path, target.RelativePath);
                    var targetInfo = targetPath == null ? null : new FileInfo(targetPath);
                    if (target?.Id != relinkId || targetInfo is not { Exists: true } || targetInfo.Length != target.Size
                        || targetInfo.LastWriteTimeUtc != target.ModifiedAt || SameFile(HealthPaths.Resolve(root.Path, file.RelativePath), targetPath!) != FileSameness.Different
                        || !await HashMatchesAsync(targetPath!, file.ContentHash!, ct))
                        throw new HealthRefusedException("error.health.relinkTargetChanged");
                }
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
                        op.Status = "failed"; op.ErrorKey = "health.operation.error.deleteFailed";
                        await db.SaveChangesAsync(CancellationToken.None);
                        throw;
                    }
                }
                TestHookAfterFileDeleted?.Invoke();
                // Past this point the archive is gone; a client disconnect or request cancellation
                // must not stop the DB cleanup from finishing, or the chapter links point at nothing
                // until the next restart's recovery pass. CancellationToken.None makes the cleanup
                // itself uninterruptible; the catch is a second net for anything upstream that still
                // throws OperationCanceledException, so recovery runs now instead of waiting.
                try { await CompleteDeletionAsync(op, file, root, CancellationToken.None); }
                catch (OperationCanceledException) { await RecoverAsync(CancellationToken.None); throw; }
                return;
            }
            var original = HealthPaths.Resolve(root.Path, file.RelativePath);
            var candidates = Candidates(op);
            if (op.Kind == "repair")
            {
                if (chapters.Count == 0 || candidates.Count != chapters.Count || !chapters.All(c => candidates.Count(p => p.ChapterId == c.Id) == 1))
                    throw new HealthRefusedException("error.health.replacementIncomplete");
                foreach (var candidate in candidates)
                {
                    var path = HealthPaths.Resolve(root.Path, candidate.RelativePath);
                    await using var stream = File.OpenRead(path);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != candidate.Hash)
                        throw new HealthRefusedException("error.health.candidateChanged");
                    if (candidate.Analysis.Status != "complete" || candidate.Analysis.Problems.Any(p => p.Severity == "error"))
                        throw new HealthRefusedException("error.health.candidateInvalid");
                }

            }
            var reset = op.Kind == "repair" && RequiresReset(file, candidates, chapters.Count);
            if (reset && !resetPositions) throw new HealthRefusedException("error.health.resetConfirmationRequired");
            var rollbackRelative = $".maki/health/{op.Id}/original.cbz";
            var rollback = HealthPaths.Resolve(root.Path, rollbackRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
            candidates = candidates.Select(c => c with { FinalPath = Path.Combine(Path.GetDirectoryName(file.RelativePath) ?? "", $"health-{op.Id}-chapter-{c.ChapterId}.cbz") }).ToList();
            foreach (var candidate in candidates)
                if (File.Exists(HealthPaths.Resolve(root.Path, candidate.FinalPath!))) throw new HealthRefusedException("error.health.destinationExists");
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
                        replacement.ReplacedAtUtc = replacement.DateAdded;
                        var group = mapping == null ? null : await db.ChapterSourceLinks
                            .Where(l => l.ChapterId == chapter.Id && l.SourceMappingId == mapping.Id)
                            .Select(l => l.Group)
                            .FirstOrDefaultAsync(ct);
                        ChapterFileQualityService.StampTierOnly(replacement, quality.KindOf(replacement.SourceName), group);
                        db.ChapterFiles.Add(replacement);
                        await db.SaveChangesAsync(ct);
                        chapter.ChapterFileId = replacement.Id;
                        if (reset)
                        {
                            db.ReaderBookmarks.RemoveRange(await db.ReaderBookmarks.IgnoreQueryFilters().Where(b => b.ChapterId == chapter.Id).ToListAsync(ct));
                            // Unlike a bookmark, a note is still worth keeping when its page moved.
                            foreach (var note in await db.ChapterNotes.IgnoreQueryFilters().Where(n => n.ChapterId == chapter.Id && n.PageIndex != null).ToListAsync(ct))
                                note.PageIndex = null;
                            foreach (var progress in await db.ChapterProgress.IgnoreQueryFilters().Where(p => p.ChapterId == chapter.Id).ToListAsync(ct))
                            { progress.PageIndex = 0; progress.PageCount = candidate.Analysis.Pages.Count; }
                        }
                    }
                }
                // Include stale file rows even if no chapter still references them.
                var oldFiles = await HealthInventory.RecordsAtAsync(db, root.Id, file.RelativePath, ct);
                // Same pages in the same places: a copy of the old archive on a KOReader device still reads true.
                if (op.Kind == "repair" && !reset && chapters.Single().ChapterFileId is { } replacementId)
                    await KoreaderDocumentIndexer.MoveAsync(db, oldFiles.Select(f => f.Id).ToList(), replacementId, ct);
                db.ChapterFiles.RemoveRange(oldFiles);
                foreach (var old in oldFiles) Invalidate(old.Id);
                file.Removed = true;
                foreach (var finding in await db.HealthFindings.Where(f => f.FileId == file.Id).ToListAsync(ct)) finding.State = "resolved";
                op.Status = "completed"; op.FinishedAt = DateTime.UtcNow;
                db.HealthHistory.Add(new()
                {
                    Kind = op.Kind,
                    FileId = file.Id,
                    UserId = op.UserId,
                    MessageKey = "health.history.operationCompleted",
                    // {kind} is the operation's own name ("repair", "delete"), a stored value the
                    // frontend already has its own words for. Named, not translated here.
                    ParamsJson = JsonSerializer.Serialize(new { kind = op.Kind, path = file.RelativePath }),
                });
                db.HealthScans.Add(new() { RootFolderId = root.Id });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                HealthWorker.Wake();
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

    /// <summary>
    /// Whether replacing this file moves the pages under a reader's bookmarks and resume positions.
    /// </summary>
    /// <remarks>
    /// Compared by the bytes of each page. A replacement that re-encoded a page without changing
    /// what it shows now counts as a change, where a pixel comparison would have seen through it -
    /// that comparison went with the decoding it needed. The cost of being wrong this way is one
    /// checkbox on a rare replacement; the other way it silently moves everyone's place.
    /// </remarks>
    public static bool RequiresReset(HealthFile file, List<RepairCandidate> candidates, int chapters)
    {
        if (chapters != 1 || candidates.Count != 1) return true;
        var old = HealthScanService.Analysis(file).Pages;
        var next = candidates[0].Analysis.Pages;
        return old.Count != next.Count || old.Count == 0 || old.Zip(next).Any(p => p.First.RawHash != p.Second.RawHash);
    }

    /// <summary>
    /// Removes a cancelled operation's staging directory (candidate downloads, and the rollback
    /// copy if apply got that far). Best-effort: an unavailable root just means the sweep on the
    /// next startup gets another chance, the same as any other recovery path here.
    /// </summary>
    public async Task RemoveStagingAsync(HealthOperation op, CancellationToken ct)
    {
        var file = await db.HealthFiles.FindAsync([op.FileId], ct);
        var root = file == null ? null : await db.RootFolders.FindAsync([file.RootFolderId], ct);
        if (root == null || !Directory.Exists(root.Path)) return;
        var staging = HealthPaths.Resolve(root.Path, $".maki/health/{op.Id}");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var op in await db.HealthOperations.Where(o => o.Status == "cancelled").ToListAsync(ct))
        {
            try { await RemoveStagingAsync(op, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        foreach (var op in await db.HealthOperations.Where(o => o.Status == "deleting").ToListAsync(ct))
        {
            var file = await db.HealthFiles.FindAsync([op.FileId], ct) ?? throw new IOException("Missing deletion journal file");
            var root = await db.RootFolders.FindAsync([file.RootFolderId], ct) ?? throw new IOException("Missing deletion journal root");
            if (!Directory.Exists(root.Path)) throw new IOException("Cannot recover deletion while root is unavailable");
            if (File.Exists(HealthPaths.Resolve(root.Path, file.RelativePath)))
            { op.Status = "failed"; op.ErrorKey = "health.operation.error.deleteInterrupted"; await db.SaveChangesAsync(ct); }
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
            op.Status = "failed"; op.ErrorKey = "health.operation.error.applyInterrupted";
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task CompleteDeletionAsync(HealthOperation op, HealthFile file, RootFolder root, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var oldFiles = await HealthInventory.RecordsAtAsync(db, root.Id, file.RelativePath, ct);
        var ids = oldFiles.Select(f => f.Id).ToArray();
        var chapters = await db.Chapters.Where(c => c.ChapterFileId != null && ids.Contains(c.ChapterFileId.Value)).ToListAsync(ct);
        // Recovery after a crash lands here too, by which time the copy may have gone as well.
        var target = op.RelinkFileId is { } relinkId ? await db.HealthFiles.FindAsync([relinkId], ct) : null;
        if (target != null && (target.Removed || !File.Exists(HealthPaths.Resolve(root.Path, target.RelativePath)))) target = null;
        if (target != null && chapters.Count > 0)
        {
            // The row moves rather than being rebuilt, so the chapters keep their file id and with it
            // everything keyed on it: quality, conversion state, KOReader documents.
            var kept = oldFiles.First(f => chapters.Any(c => c.ChapterFileId == f.Id));
            kept.RelativePath = target.RelativePath;
            // Whether a file is hardlinked belongs to the path, so conversion has to look again.
            if (kept.CheckedWith == PlanAction.HardSkipName) { kept.CheckedWith = null; kept.ConversionCheckedAtUtc = null; }
            foreach (var chapter in chapters) chapter.ChapterFileId = kept.Id;
            target.ChapterFileId = kept.Id;
            target.SeriesId = kept.SeriesId;
            foreach (var finding in await db.HealthFindings.Where(f => f.FileId == target.Id && f.Kind == "unlinked" && f.State != "resolved").ToListAsync(ct))
                finding.State = "resolved";
            oldFiles.Remove(kept);
            Invalidate(kept.Id);
        }
        else
        {
            target = null;
            foreach (var chapter in chapters) chapter.ChapterFileId = null;
        }
        db.ChapterFiles.RemoveRange(oldFiles);
        foreach (var old in oldFiles) Invalidate(old.Id);
        file.Removed = true;
        foreach (var finding in await db.HealthFindings.Where(f => f.FileId == file.Id).ToListAsync(ct)) finding.State = "resolved";
        op.Status = "completed"; op.FinishedAt = DateTime.UtcNow;
        db.HealthHistory.Add(new()
        {
            Kind = "delete", FileId = file.Id, UserId = op.UserId,
            MessageKey = target != null ? "health.history.deletedRelinked" : file.Size < 0 ? "health.history.deletedMissing" : "health.history.deletedFile",
            ParamsJson = target != null
                ? JsonSerializer.Serialize(new { path = file.RelativePath, target = target.RelativePath })
                : JsonSerializer.Serialize(new { path = file.RelativePath }),
        });
        await db.SaveChangesAsync(ct);
        if (file.ContentHash != null) await HealthDuplicates.ResolveLonelyAsync(db, [file.ContentHash], ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// A spare is only spare while nothing reads from it and another copy of its bytes is still on
    /// disk. Checked at apply because the review can be stale: a relink can give the spare chapters,
    /// and the copy it was spare to can have been deleted since. A copy counts only when the
    /// filesystem proves it is another file: two roots can name one share, and a case-insensitive
    /// mount opens two spellings as one file.
    /// </summary>
    private async Task RequireSpareAsync(HealthFile file, RootFolder root, List<Chapter> chapters, CancellationToken ct)
    {
        if (chapters.Count > 0 || file.ChapterFileId != null) throw new HealthRefusedException("error.health.spareInUse");
        if (file.Size < 0 || file.ContentHash == null) throw new HealthRefusedException("error.health.spareLastCopy");
        var path = HealthPaths.Resolve(root.Path, file.RelativePath);
        var copies = await db.HealthFiles.AsNoTracking()
            .Where(f => f.Id != file.Id && !f.Removed && f.Size >= 0 && f.ContentHash == file.ContentHash)
            .OrderByDescending(f => f.ChapterFileId != null).ThenBy(f => f.Id)
            .ToListAsync(ct);
        var undistinguished = false;
        foreach (var copy in copies)
        {
            var copyRoot = copy.RootFolderId == root.Id ? root : await db.RootFolders.FindAsync([copy.RootFolderId], ct);
            if (copyRoot == null || !Directory.Exists(copyRoot.Path) || LibraryPaths.ResolveNoLinks(copyRoot.Path, copy.RelativePath) is not { } copyPath) continue;
            var info = new FileInfo(copyPath);
            if (!info.Exists || info.Length != copy.Size || info.LastWriteTimeUtc != copy.ModifiedAt) continue;
            if (!Distinct(root, file.RelativePath, path, copyRoot, copy.RelativePath, copyPath))
            {
                undistinguished = true;
                continue;
            }
            if (await HashMatchesAsync(copyPath, file.ContentHash, ct)) return;
        }
        throw new HealthRefusedException(undistinguished ? "error.health.spareNotDistinct" : "error.health.spareLastCopy");
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> and a copy elsewhere are provably two files: the
    /// filesystem says so, and a copy in another root is not on the same storage either.
    /// </summary>
    private bool Distinct(RootFolder root, string relative, string path, RootFolder otherRoot, string otherRelative, string otherPath) =>
        SameFile(path, otherPath) == FileSameness.Different
        && (otherRoot.Id == root.Id || !SharesStorage(root, relative, otherRoot, otherRelative));

    /// <summary>Verdicts of <see cref="Probe"/> for this service's scope: one request, so one bulk operation.</summary>
    private readonly Dictionary<(int, int, string), bool> sharedStorage = [];

    /// <summary>
    /// Whether two root folders may be one storage under two names. Two CIFS mounts of one share
    /// report different devices, so file identity calls every file in one a separate copy of its
    /// twin in the other. A probe written in one root that shows up in the other settles it. When
    /// one copy's path ends with the other's, the leftover folders say where one root would sit
    /// inside the other (a mount of a subfolder of the share), and that spot is probed too. Probes
    /// are only ever written in a root's own <c>.maki/tmp</c>, and one that cannot be written or
    /// looked for counts as shared.
    /// </summary>
    private bool SharesStorage(RootFolder root, string relative, RootFolder other, string otherRelative)
    {
        if (Probe(root, other, "")) return true;
        var mine = LibraryPaths.ComparisonKey(relative).Split('/');
        var theirs = LibraryPaths.ComparisonKey(otherRelative).Split('/');
        if (Prefix(theirs, mine) is { } rootInOther && Probe(root, other, rootInOther)) return true;
        return Prefix(mine, theirs) is { } otherInRoot && Probe(other, root, otherInRoot);
    }

    /// <summary>The leading folders of <paramref name="longer"/> when the rest of it is <paramref name="shorter"/>, ignoring case.</summary>
    private static string? Prefix(string[] longer, string[] shorter) =>
        longer.Length > shorter.Length && longer.TakeLast(shorter.Length).SequenceEqual(shorter, StringComparer.OrdinalIgnoreCase)
            ? string.Join('/', longer.Take(longer.Length - shorter.Length))
            : null;

    /// <summary>
    /// Writes a probe in <paramref name="writeIn"/>'s <c>.maki/tmp</c> and looks for it in
    /// <paramref name="lookIn"/> at <paramref name="folder"/><c>/.maki/tmp</c>, by listing the folder:
    /// a CIFS client can answer a single lookup from its cache. Folders it created are removed again.
    /// </summary>
    private bool Probe(RootFolder writeIn, RootFolder lookIn, string folder)
    {
        var key = (writeIn.Id, lookIn.Id, folder.ToUpperInvariant());
        if (sharedStorage.TryGetValue(key, out var known)) return known;
        var name = $"probe-{Guid.NewGuid():N}.tmp";
        string? probe = null;
        var created = new List<string>();
        var shared = true;
        try
        {
            probe = HealthPaths.Resolve(writeIn.Path, $".maki/tmp/{name}");
            var seenIn = Path.GetDirectoryName(HealthPaths.Resolve(lookIn.Path, folder.Length == 0 ? $".maki/tmp/{name}" : $"{folder}/.maki/tmp/{name}"))!;
            var tmp = Path.GetDirectoryName(probe)!;
            var maki = Path.GetDirectoryName(tmp)!;
            if (!Directory.Exists(maki)) created.Add(maki);
            if (!Directory.Exists(tmp)) created.Insert(0, tmp);
            Directory.CreateDirectory(tmp);
            File.WriteAllBytes(probe, []);
            try { shared = Directory.EnumerateFiles(seenIn).Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)); }
            catch (DirectoryNotFoundException) { shared = false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        finally
        {
            try
            {
                if (probe != null) File.Delete(probe);
                foreach (var made in created)
                    if (Directory.Exists(made) && !Directory.EnumerateFileSystemEntries(made).Any()) Directory.Delete(made);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return sharedStorage[key] = shared;
    }

    /// <summary>
    /// Refuses to delete a file that a record anywhere may read under another name: another
    /// spelling on a case-insensitive mount, another root naming the same share, or a root mounted
    /// as a folder of another. Such a twin has the same bytes by definition, so the linked copies of
    /// the file's hash are the whole list; each must provably be another file. A file with no hash
    /// yet falls back to the records of its own root with the same file name.
    /// </summary>
    private async Task RefuseIfAnotherRecordReadsItAsync(HealthFile file, RootFolder root, CancellationToken ct)
    {
        var path = HealthPaths.Resolve(root.Path, file.RelativePath);
        var own = (await HealthInventory.RecordsAtAsync(db, root.Id, file.RelativePath, ct)).Select(f => f.Id).ToHashSet();
        if (file.ContentHash == null)
        {
            foreach (var record in await HealthInventory.RecordsNamedAsync(db, root.Id, Path.GetFileName(LibraryPaths.ComparisonKey(file.RelativePath)), ct))
            {
                if (own.Contains(record.Id) || LibraryPaths.Resolve(root.Path, record.RelativePath) is not { } other || !File.Exists(other)) continue;
                if (SameFile(path, other) != FileSameness.Different) throw new HealthRefusedException("error.health.fileBacksChapters");
            }
            return;
        }

        // Twins by the current hash, by a hash an earlier version had (a scan of an empty mount point
        // records the file missing and can drop its hash), and, for rows never verified, by name and size.
        var hash = file.ContentHash;
        var name = Path.GetFileName(LibraryPaths.ComparisonKey(file.RelativePath));
        var twins = (await db.HealthFiles.AsNoTracking()
                .Where(f => f.Id != file.Id && !f.Removed && f.ChapterFileId != null
                    && (f.ContentHash == hash
                        || db.HealthFileVersions.Any(v => v.FileId == f.Id && v.ContentHash == hash)
                        || (f.ContentHash == null && f.Size == file.Size)))
                .Select(f => new { f.RootFolderId, f.RelativePath, f.Size, f.ContentHash, ChapterFileId = f.ChapterFileId!.Value })
                .ToListAsync(ct))
            .Where(t => t.ContentHash != null || t.Size != file.Size
                || string.Equals(Path.GetFileName(LibraryPaths.ComparisonKey(t.RelativePath)), name, StringComparison.OrdinalIgnoreCase))
            .Where(t => !own.Contains(t.ChapterFileId));
        foreach (var twin in twins)
        {
            var twinRoot = twin.RootFolderId == root.Id ? root : await db.RootFolders.FindAsync([twin.RootFolderId], ct);
            if (twinRoot == null) continue;
            // A root that cannot be looked at may be this one under another name, and a twin that reads
            // as missing may be this file behind a mount that was not there when it was looked for.
            if (twin.Size < 0 || !Directory.Exists(twinRoot.Path) || LibraryPaths.Resolve(twinRoot.Path, twin.RelativePath) is not { } other
                || !File.Exists(other) || !Distinct(root, file.RelativePath, path, twinRoot, twin.RelativePath, other))
                throw new HealthRefusedException("error.health.fileBacksChapters");
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string hash, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) == hash;
    }

    private void Invalidate(int id)
    {
        archives.Invalidate(id);
        if (paths == null) return;
        var directory = Path.Combine(paths.ReaderCacheDir, id.ToString());
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
