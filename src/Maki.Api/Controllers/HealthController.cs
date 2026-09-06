using System.Text.Json;
using System.Security.Cryptography;
using Maki.Api.Auth;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/health")]
public class HealthController(MakiDbContext db, HealthMonitor monitor, HealthOperationService operations,
    HealthMatchService matches, ICurrentUser user, IAppSettings settings) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Overview(CancellationToken ct) => Ok(new
    {
        checks = await db.HealthChecks.OrderBy(c => c.Category).ThenBy(c => c.Id).ToListAsync(ct),
        openFindings = await db.HealthFindings.CountAsync(f => f.State == "open", ct),
        files = await db.HealthFiles.CountAsync(f => !f.Removed, ct),
        scans = await db.HealthScans.OrderByDescending(s => s.Id).Take(10).ToListAsync(ct),
        roots = await db.RootFolders.Select(r => new { r.Id, r.Path }).ToListAsync(ct)
    });

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct) { await monitor.RefreshAsync(ct); return Ok(new { refreshed = true }); }

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct) => Ok(JsonSerializer.Deserialize<HealthOptions>(await settings.GetAsync("health.options", ct) ?? "{}", HealthScanService.Json) ?? new());

    [HttpPut("options")]
    public async Task<IActionResult> Options(HealthOptions options, CancellationToken ct)
    {
        if (options.ScanHour is < 0 or > 23 || options.BackupDays is < 1 or > 365 ||
            options.ErrorPercent < 0 || options.WarningPercent > 100 || options.WarningPercent < options.ErrorPercent ||
            options.ErrorGiB < 0 || options.WarningGiB < options.ErrorGiB)
            return BadRequest(new { message = "Invalid health thresholds or schedule" });
        try { TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone ?? TimeZoneInfo.Local.Id); }
        catch { return BadRequest(new { message = "Unknown timezone" }); }
        await settings.SetAsync("health.options", JsonSerializer.Serialize(options, HealthScanService.Json), ct);
        return Ok(options);
    }

    [HttpPost("checks/acknowledge")]
    public async Task<IActionResult> Acknowledge(CheckReview request, CancellationToken ct)
    {
        var check = await db.HealthChecks.FindAsync([request.Id], ct);
        if (check == null) return NotFound();
        check.Acknowledged = request.Acknowledged;
        await db.SaveChangesAsync(ct);
        return Ok(check);
    }
    public record CheckReview(string Id, bool Acknowledged);

    [HttpGet("files")]
    public async Task<IActionResult> Files([FromQuery] int page = 1, [FromQuery] string? search = null,
        [FromQuery] int? rootId = null, [FromQuery] string? kind = null, [FromQuery] string? state = null, CancellationToken ct = default)
    {
        var query = db.HealthFiles.Where(f => !f.Removed);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(f => f.RelativePath.Contains(search));
        if (rootId != null) query = query.Where(f => f.RootFolderId == rootId);
        if (kind != null || state != null) query = query.Where(f => db.HealthFindings.Any(i => i.FileId == f.Id && i.Version == f.Version && (kind == null || i.Kind == kind) && (state == null || i.State == state)));
        var total = await query.CountAsync(ct);
        var files = await query.OrderBy(f => f.RelativePath).Skip((Math.Max(page, 1) - 1) * 30).Take(30).ToListAsync(ct);
        var ids = files.Select(f => f.Id).ToArray();
        var findings = await db.HealthFindings.Where(f => ids.Contains(f.FileId) && f.State != "resolved").ToListAsync(ct);
        return Ok(new { total, items = files.Select(f => new { f.Id, f.RelativePath, f.Version, f.RootFolderId, f.SeriesId, f.ChapterFileId, f.Size, f.ContentHash, f.Status, f.AnalyzedAt, findings = findings.Where(i => i.FileId == f.Id && i.Version == f.Version) }) });
    }

    [HttpGet("files/{id:int}")]
    public async Task<IActionResult> FileDetail(int id, CancellationToken ct)
    {
        var file = await db.HealthFiles.FindAsync([id], ct);
        if (file == null) return NotFound();
        var chapters = await db.Chapters.Where(c => c.ChapterFileId == file.ChapterFileId && file.ChapterFileId != null).Select(c => new { c.Id, c.Title, c.Number, c.Wanted }).ToListAsync(ct);
        // Priority order, so the list reads as the order an automatic request would try them in.
        var mappings = await db.SourceMappings.Where(m => m.SeriesId == file.SeriesId && m.Enabled)
            .OrderBy(m => m.Priority).ThenBy(m => m.Id).Select(m => new { m.Id, m.SourceName, m.Priority }).ToListAsync(ct);
        return Ok(new { file, analysis = HealthScanService.Analysis(file), chapters, mappings, match = await matches.MatchAsync(file, ct), findings = await db.HealthFindings.Where(f => f.FileId == id && f.Version == file.Version).ToListAsync(ct) });
    }

    [HttpGet("files/{id:int}/pages/{page:int}")]
    public async Task<IActionResult> Preview(int id, int page, [FromQuery] string version, CancellationToken ct)
    {
        var file = await db.HealthFiles.FindAsync([id], ct);
        if (file == null || file.Removed) return NotFound();
        if (file.Version != version) return Conflict(new { message = "Preview is stale" });
        var root = await db.RootFolders.FindAsync([file.RootFolderId], ct);
        if (root == null) return NotFound();
        var analysis = HealthScanService.Analysis(file);
        if (page < 0 || page >= analysis.Pages.Count) return NotFound();
        try
        {
            var path = HealthPaths.Resolve(root.Path, file.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAt) return Conflict();
            return await VerifiedPreview(path, file.ContentHash, analysis.Pages[page], ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { return Conflict(new { message = "Preview is unavailable; rescan" }); }
    }

    public record FindingReview(string Version, string State);
    [HttpPut("findings/{id:int}")]
    public async Task<IActionResult> ReviewFinding(int id, FindingReview request, CancellationToken ct)
    {
        if (request.State is not ("open" or "acknowledged" or "ignored")) return BadRequest();
        var finding = await db.HealthFindings.FindAsync([id], ct);
        if (finding == null) return NotFound();
        var file = await db.HealthFiles.FindAsync([finding.FileId], ct);
        if (finding.Version != request.Version || file?.Version != request.Version) return Conflict();
        finding.State = request.State;
        db.HealthHistory.Add(new() { FileId = finding.FileId, UserId = user.UserId, Kind = "review", Message = $"{finding.Kind}: {request.State}" });
        await db.SaveChangesAsync(ct);
        return Ok(finding);
    }

    public record BulkFindingReview(int[] FileIds, string State, string? Kind = null);
    /// <summary>
    /// Moves every open finding on the selected files to one state, so a reviewer can clear a
    /// filtered page in one action instead of opening thirty archives.
    /// </summary>
    /// <remarks>
    /// Findings whose version is no longer the version on disk are skipped rather than carried
    /// over: an ignore decision belongs to the content it was made about, and a bulk action must
    /// not be the one thing in the workspace that launders it onto new bytes.
    /// </remarks>
    [HttpPost("findings/review")]
    public async Task<IActionResult> ReviewFindings(BulkFindingReview request, CancellationToken ct)
    {
        if (request.State is not ("open" or "acknowledged" or "ignored")) return BadRequest(new { message = "Unknown finding state" });
        if (request.FileIds is not { Length: > 0 and <= 500 }) return BadRequest(new { message = "Select between 1 and 500 files" });
        var files = await db.HealthFiles.Where(f => request.FileIds.Contains(f.Id) && !f.Removed).ToListAsync(ct);
        var ids = files.Select(f => f.Id).ToList();
        var findings = await db.HealthFindings.Where(f => ids.Contains(f.FileId) && f.State != "resolved").ToListAsync(ct);
        var updated = 0;
        var stale = 0;
        foreach (var finding in findings)
        {
            if (request.Kind != null && finding.Kind != request.Kind) continue;
            if (finding.Version != files.First(f => f.Id == finding.FileId).Version) { stale++; continue; }
            if (finding.State == request.State) continue;
            finding.State = request.State;
            db.HealthHistory.Add(new() { FileId = finding.FileId, UserId = user.UserId, Kind = "review", Message = $"{finding.Kind}: {request.State}" });
            updated++;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { updated, stale });
    }

    public record ImportRequest(int[] FileIds);
    /// <summary>
    /// Adopts unlinked archives through the ordinary library import, one rescan per series folder
    /// the selection touches.
    /// </summary>
    /// <remarks>
    /// This is the user asking for an import, not the repair pipeline taking one: repair candidates
    /// are still barred from every ordinary import path. A rescan reconciles the whole series folder
    /// rather than the selected files alone, which is deliberate - it is the same operation the
    /// series page runs, and half-importing a folder leaves exactly the state the reviewer came here
    /// to clear. The follow-up scan re-analyses the selection so the unlinked findings resolve
    /// instead of sitting open until the nightly run.
    /// </remarks>
    [HttpPost("imports")]
    public Task<IActionResult> Import(ImportRequest request, [FromServices] CbzLinkService cbz, CancellationToken ct) => ConflictGuard(async () =>
    {
        if (request.FileIds is not { Length: > 0 and <= 500 }) return BadRequest(new { message = "Select between 1 and 500 files" });
        var files = await db.HealthFiles.Where(f => request.FileIds.Contains(f.Id) && !f.Removed && f.ChapterFileId == null).ToListAsync(ct);
        var owners = new Dictionary<int, Series>();
        var orphans = 0;
        foreach (var file in files)
        {
            var owner = await matches.OwnerAsync(file, ct);
            if (owner?.RootFolder == null) { orphans++; continue; }
            owners[owner.Id] = owner;
        }
        var linked = 0;
        var unrecognized = 0;
        foreach (var owner in owners.Values)
        {
            var result = await cbz.RescanSeriesAsync(owner, ct);
            linked += result.NewFiles + result.Relinked;
            unrecognized += result.Unrecognized;
        }
        if (files.Count > 0)
        {
            db.HealthScans.Add(new HealthScan { FileIdsJson = JsonSerializer.Serialize(files.Select(f => f.Id).ToArray()) });
            db.HealthHistory.Add(new() { UserId = user.UserId, Kind = "import", Message = $"Imported {files.Count} archives across {owners.Count} series: {linked} linked, {unrecognized} unrecognized" });
            await db.SaveChangesAsync(ct);
        }
        return Ok(new { files = files.Count, series = owners.Count, linked, unrecognized, orphans });
    });

    public record ScanRequest(int? RootFolderId = null, int? SeriesId = null, int[]? FileIds = null, bool Force = false);
    [HttpPost("scans")]
    public async Task<IActionResult> Scan(ScanRequest request, CancellationToken ct)
    {
        if (request.FileIds?.Length > 500) return BadRequest();
        var scan = new HealthScan { RootFolderId = request.RootFolderId, SeriesId = request.SeriesId, FileIdsJson = JsonSerializer.Serialize(request.FileIds ?? []), Force = request.Force };
        db.HealthScans.Add(scan); await db.SaveChangesAsync(ct); return Accepted(scan);
    }
    [HttpPost("scans/{id:int}/cancel")]
    public async Task<IActionResult> CancelScan(int id, CancellationToken ct)
    {
        var scan = await db.HealthScans.FindAsync([id], ct);
        if (scan == null) return NotFound();
        if (scan.Status is "pending" or "running") scan.Status = "cancelled";
        await db.SaveChangesAsync(ct);
        if (HealthScanService.Running.TryGetValue(id, out var cancellation))
        { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        return Ok(scan);
    }

    public record FileReview(int FileId, string Version, int? SourceMappingId = null);
    public record ApplyReview(string Version, bool Confirmed, bool ResetPositions = false);
    [HttpPost("repairs")]
    public Task<IActionResult> Repair(FileReview request, CancellationToken ct) => ConflictGuard(async () =>
        Ok(await operations.RequestAsync(request.FileId, request.Version, request.SourceMappingId, user.UserId, ct)));
    public record BulkDelete(int[] FileIds, bool Confirmed);
    /// <summary>
    /// Deletes several archives under one confirmation, each through the ordinary preview-then-apply
    /// path so nothing skips validation.
    /// </summary>
    /// <remarks>
    /// One confirmation for the batch, not none: the single-file flow's checkbox exists so a
    /// deletion is never a stray click, and a caller that has named the files and confirmed the
    /// count has cleared that bar. Every file is still validated individually, so one whose bytes
    /// changed since the review is refused and reported instead of taking the batch down with it.
    /// The cap is lower than the other bulk actions because this one cannot be undone.
    /// </remarks>
    [HttpPost("deletions/bulk")]
    public async Task<IActionResult> DeleteBulk(BulkDelete request, CancellationToken ct)
    {
        if (!request.Confirmed) return BadRequest(new { message = "Explicit confirmation is required" });
        if (request.FileIds is not { Length: > 0 and <= 100 }) return BadRequest(new { message = "Select between 1 and 100 files" });
        var files = await db.HealthFiles.Where(f => request.FileIds.Contains(f.Id) && !f.Removed).ToListAsync(ct);
        var deleted = 0;
        var failures = new List<object>();
        foreach (var file in files)
        {
            try
            {
                var op = await operations.PreviewDeleteAsync(file.Id, file.Version, user.UserId, ct);
                await operations.ApplyAsync(op.Id, file.Version, true, false, ct);
                deleted++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failures.Add(new { file.Id, file.RelativePath, message = ex.Message });
            }
        }
        db.HealthHistory.Add(new() { UserId = user.UserId, Kind = "delete", Message = $"Bulk deletion: {deleted} archives removed, {failures.Count} refused" });
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted, failures });
    }

    [HttpPost("deletions/preview")]
    public Task<IActionResult> DeletePreview(FileReview request, CancellationToken ct) => ConflictGuard(async () =>
        Ok(await operations.PreviewDeleteAsync(request.FileId, request.Version, user.UserId, ct)));
    [HttpPost("operations/{id:int}/apply")]
    public Task<IActionResult> Apply(int id, ApplyReview request, CancellationToken ct) => ConflictGuard(async () =>
    { await operations.ApplyAsync(id, request.Version, request.Confirmed, request.ResetPositions, ct); return Ok(new { applied = true }); });

    [HttpGet("operations")]
    public async Task<IActionResult> Operations([FromQuery] int page = 1, CancellationToken ct = default) => Ok(new
    {
        total = await db.HealthOperations.CountAsync(ct),
        items = await db.HealthOperations.OrderByDescending(o => o.Id).Skip((Math.Max(1, page) - 1) * 30).Take(30).ToListAsync(ct)
    });
    [HttpGet("operations/{id:int}")]
    public async Task<IActionResult> Operation(int id, CancellationToken ct)
    {
        var op = await db.HealthOperations.FindAsync([id], ct);
        if (op == null) return NotFound();
        var file = await db.HealthFiles.FindAsync([op.FileId], ct);
        var chapters = await db.Chapters.Where(c => file != null && file.ChapterFileId != null && c.ChapterFileId == file.ChapterFileId).Select(c => new { c.Id, c.Title, c.Wanted }).ToListAsync(ct);
        var candidates = HealthOperationService.Candidates(op);
        return Ok(new { operation = op, file, chapters, candidates, requiresReset = file != null && HealthOperationService.RequiresReset(file, candidates, chapters.Count) });
    }
    [HttpGet("operations/{id:int}/candidates/{chapterId:int}/pages/{page:int}")]
    public async Task<IActionResult> CandidatePreview(int id, int chapterId, int page, CancellationToken ct)
    {
        var op = await db.HealthOperations.FindAsync([id], ct);
        if (op?.Status != "review") return NotFound();
        var file = await db.HealthFiles.FindAsync([op.FileId], ct);
        var root = file == null ? null : await db.RootFolders.FindAsync([file.RootFolderId], ct);
        var candidate = HealthOperationService.Candidates(op).FirstOrDefault(c => c.ChapterId == chapterId);
        if (root == null || candidate == null || page < 0 || page >= candidate.Analysis.Pages.Count) return NotFound();
        try { return await VerifiedPreview(HealthPaths.Resolve(root.Path, candidate.RelativePath), candidate.Hash, candidate.Analysis.Pages[page], ct); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { return Conflict(new { message = "Candidate preview is unavailable" }); }
    }
    [HttpPost("operations/{id:int}/cancel")]
    public async Task<IActionResult> CancelOperation(int id, CancellationToken ct)
    {
        await HealthOperationService.MutationGate.WaitAsync(ct);
        try
        {
            var op = await db.HealthOperations.FindAsync([id], ct);
            if (op == null) return NotFound();
            if (op.Status is "applying" or "deleting") return Conflict();
            if (!HealthOperationService.Terminal(op.Status))
            {
                op.Status = "cancelled";
                foreach (var item in await db.DownloadQueue.Where(q => q.HealthOperationId == id && q.Status != QueueStatus.Completed).ToListAsync(ct))
                { item.Status = QueueStatus.Cancelled; HttpContext.RequestServices.GetRequiredService<DownloadQueueService>().CancelWork(item.Id); }
                await db.SaveChangesAsync(ct);
            }
            return Ok(op);
        }
        finally { HealthOperationService.MutationGate.Release(); }
    }
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int page = 1, CancellationToken ct = default) => Ok(new
    {
        total = await db.HealthHistory.CountAsync(ct),
        items = await db.HealthHistory.OrderByDescending(h => h.Id).Skip((Math.Max(1, page) - 1) * 30).Take(30).ToListAsync(ct)
    });
    private async Task<IActionResult> ConflictGuard(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { return Conflict(new { message = ex.Message }); }
    }

    private async Task<IActionResult> VerifiedPreview(string path, string? hash, PageFingerprint page, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (hash == null || Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != hash) return Conflict(new { message = "Preview content changed" });
        input.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read, true);
        var entry = archive.GetEntry(page.Name);
        if (entry == null || entry.Length > ArchiveHealthAnalyzer.MaxEntryBytes) return NotFound();
        using var output = new MemoryStream();
        await using var content = entry.Open();
        var buffer = new byte[65536];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > ArchiveHealthAnalyzer.MaxEntryBytes) return BadRequest();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        var bytes = output.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != page.RawHash) return Conflict();
        Response.Headers.CacheControl = "no-store";
        return File(bytes, CbzReader.ContentType(page.Name));
    }

    [HttpGet("scans/{id:int}")]
    public async Task<IActionResult> ScanStatus(int id, CancellationToken ct) =>
        await db.HealthScans.FindAsync([id], ct) is {} scan ? Ok(scan) : NotFound();

    [HttpGet("findings")]
    public async Task<IActionResult> Findings([FromQuery] int? fileId = null, [FromQuery] string? state = null, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var query = db.HealthFindings.Where(f => (fileId == null || f.FileId == fileId) && (state == null || f.State == state));
        return Ok(new { total = await query.CountAsync(ct), items = await query.OrderByDescending(f => f.Id).Skip((Math.Max(1, page)-1)*30).Take(30).ToListAsync(ct) });
    }
}
