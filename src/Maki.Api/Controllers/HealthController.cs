using Maki.Api.Localization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/health")]
public class HealthController(MakiDbContext db, HealthMonitor monitor, HealthOperationService operations,
    HealthMatchService matches, ICurrentUser user, IAppSettings settings,
    ILocalizer localizer, SourceRegistry sources, HealthSourceRecovery recovery,
    ILogger<HealthController> logger) : ControllerBase
{
    /// <summary>
    /// The row as the page reads it: same fields, with <c>message</c> worded in the caller's own
    /// language. A row with no key predates this and keeps the English it was written with.
    /// </summary>
    private object Rendered(HealthCheckRecord row) => new
    {
        row.Id,
        row.Category,
        row.Status,
        Message = Render(row.MessageKey, row.ParamsJson, row.Message),
        row.Url,
        row.CheckedAt,
        row.ChangedAt,
        row.ConsecutiveFailures,
        row.NotifiedStatus,
        row.Acknowledged,
    };

    private object Rendered(HealthFinding row) => new
    {
        row.Id,
        row.FileId,
        row.Version,
        row.Kind,
        row.Severity,
        Message = Render(row.MessageKey, row.ParamsJson, row.Message),
        row.State,
        row.CreatedAt,
    };

    /// <param name="from">The status a status change left, worked out by the caller for rows
    /// written before history recorded it.</param>
    private object Rendered(HealthHistory row, IReadOnlyDictionary<string, HealthCheckRecord> checks, string? from)
    {
        var values = HealthMonitor.HealthParams(row.ParamsJson);
        return new
        {
            row.Id,
            row.CreatedAt,
            row.Kind,
            Message = RenderedHistory(row, values, checks),
            // What a scan or status change ended as, so the timeline can colour it and show the move.
            Status = values.GetValueOrDefault("status") as string,
            From = values.GetValueOrDefault("from") as string ?? from,
            row.FileId,
            row.UserId,
        };
    }

    /// <summary>
    /// A status change names the problem in the check's own words. Rows written before history
    /// stored those words borrow the check's current message while it still has the status the row
    /// recorded, so it describes the same state; otherwise they keep the bare check id. A
    /// <c>legacy:</c> check only exists while failing and keeps its problem message after it clears,
    /// so its message fits whatever the status.
    /// </summary>
    private string RenderedHistory(HealthHistory row, Dictionary<string, object?> values, IReadOnlyDictionary<string, HealthCheckRecord> checks)
    {
        if (row.MessageKey == "health.history.transition")
        {
            var detail = Render(values.GetValueOrDefault("detailKey") as string, values.GetValueOrDefault("detailParams") as string,
                values.GetValueOrDefault("detailMessage") as string ?? "");
            return localizer.Get(row.MessageKey, new { detail });
        }
        if (row.MessageKey is "health.history.degraded" or "health.history.recovered"
            && values.GetValueOrDefault("check") is string id
            && checks.GetValueOrDefault(id) is { } check
            && (check.Status == values.GetValueOrDefault("status") as string || id.StartsWith("legacy:", StringComparison.Ordinal)))
        {
            var detail = Render(check.MessageKey, check.ParamsJson, check.Message);
            return localizer.Get("health.history.transition", new { detail });
        }
        return Render(row.MessageKey, row.ParamsJson, row.Message);
    }

    private string Render(string? key, string? paramsJson, string stored)
    {
        if (key == "health.finding.joined")
        {
            var items = JsonSerializer.Deserialize<List<JoinedProblem>>(paramsJson ?? "[]", HealthScanService.Json) ?? [];
            var detail = string.Join("; ", items.Select(p => localizer.Get(p.Key, HealthMonitor.HealthParams(p.ParamsJson))));
            return localizer.Get(key, new { detail });
        }
        return key is { Length: > 0 } ? localizer.Get(key, HealthMonitor.HealthParams(paramsJson)) : stored;
    }

    /// <summary>
    /// An operation as the page reads it: same fields, with <c>error</c> worded in the caller's own
    /// language. A row with no key predates this and keeps the English it was written with.
    /// </summary>
    private object Rendered(HealthOperation op) => Rendered(op, null, null, null);

    private string? RenderedError(string? key, string? paramsJson, string? stored) =>
        key is { Length: > 0 } ? localizer.Get(key, HealthMonitor.HealthParams(paramsJson)) : stored;

    /// <summary>The row plus what it acts on, so the Repairs list can name the file and series instead of an id.</summary>
    private object Rendered(HealthOperation op, string? relativePath, Series? series, string? titleLanguage) => new
    {
        op.Id,
        op.Kind,
        op.Status,
        op.FileId,
        op.Version,
        op.SourceMappingId,
        op.UserId,
        op.JournalJson,
        Error = RenderedError(op.ErrorKey, op.ErrorParamsJson, op.Error),
        op.CreatedAt,
        op.FinishedAt,
        RelativePath = relativePath,
        SeriesId = series?.Id,
        SeriesTitle = series is null ? null : SeriesDto.DisplayTitleFor(series, titleLanguage),
        CoverUrl = series is null ? null : SeriesDto.CoverUrlFor(series.Id, series.CoverPath, series.LastMetadataRefresh),
    };

    /// <summary>
    /// An archive analysis as the page reads it: each problem's <c>message</c> worded in the
    /// caller's own language instead of the raw analyzer key.
    /// </summary>
    /// <remarks>
    /// A problem from an analysis stored before problems were keyed has no <c>MessageKey</c>; it
    /// stays readable until the next scan rewrites it, the same as the rest of <c>ArchiveAnalysis</c>.
    /// </remarks>
    private object RenderedAnalysis(ArchiveAnalysis analysis) => new
    {
        analysis.Status,
        analysis.Hash,
        analysis.Pages,
        Problems = analysis.Problems.Select(p => new
        {
            p.Kind,
            p.Severity,
            Message = p.MessageKey is { Length: > 0 } ? localizer.Get(p.MessageKey, HealthMonitor.HealthParams(p.ParamsJson)) : "",
        }),
        analysis.Verified,
    };

    [HttpGet]
    public async Task<IActionResult> Overview(CancellationToken ct) => Ok(new
    {
        checks = (await db.HealthChecks.AsNoTracking().OrderBy(c => c.Category).ThenBy(c => c.Id).ToListAsync(ct))
            .Select(Rendered),
        openFindings = await db.HealthFindings.CountAsync(f => f.State == "open", ct),
        files = await db.HealthFiles.CountAsync(f => !f.Removed, ct),
        scans = await db.HealthScans.AsNoTracking().OrderByDescending(s => s.Id).Take(10).ToListAsync(ct),
        roots = await db.RootFolders.Select(r => new { r.Id, r.Path }).ToListAsync(ct)
    });

    /// <summary>
    /// One review decision on the audit trail. Both review endpoints write the same line, so the
    /// shape lives here rather than twice.
    /// </summary>
    private static HealthHistory ReviewHistory(HealthFinding finding, int userId, string state) => new()
    {
        FileId = finding.FileId,
        UserId = userId,
        Kind = "review",
        MessageKey = "health.history.review",
        // Both are stored values the frontend already has its own words for, so they are named here
        // rather than translated: the finding's kind, and what an admin just set it to.
        ParamsJson = JsonSerializer.Serialize(new { kind = finding.Kind, state }),
    };

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct) => Ok(new { refreshed = await monitor.RefreshAsync(ct) });

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct) => Ok(JsonSerializer.Deserialize<HealthOptions>(await settings.GetAsync(SettingKeys.HealthOptions, ct) ?? "{}", HealthScanService.Json) ?? new());

    [HttpPut("options")]
    public async Task<IActionResult> Options(HealthOptions options, CancellationToken ct)
    {
        if (options.ScanHour is < 0 or > 23 || options.BackupDays is < 1 or > 365 || options.ScanWorkers is < 0 or > 32 ||
            options.ErrorPercent < 0 || options.WarningPercent > 100 || options.WarningPercent < options.ErrorPercent ||
            options.ErrorGiB < 0 || options.WarningGiB < options.ErrorGiB)
            return this.Fail(localizer, "error.health.invalidThresholds");
        try { TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone ?? TimeZoneInfo.Local.Id); }
        catch { return this.Fail(localizer, "error.health.unknownTimezone"); }
        await settings.SetAsync(SettingKeys.HealthOptions, JsonSerializer.Serialize(options, HealthScanService.Json), ct);
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

    /// <summary>A collapsed group of same-type checks, acknowledged or reopened in one go.</summary>
    [HttpPost("checks/acknowledge/bulk")]
    public async Task<IActionResult> AcknowledgeMany(CheckReviewMany request, CancellationToken ct)
    {
        var checks = await db.HealthChecks.Where(c => request.Ids.Contains(c.Id)).ToListAsync(ct);
        foreach (var check in checks) check.Acknowledged = request.Acknowledged;
        await db.SaveChangesAsync(ct);
        return Ok(new { updated = checks.Count });
    }
    public record CheckReviewMany(IReadOnlyList<string> Ids, bool Acknowledged);

    /// <summary>The series behind a grouped source warning, and how far a refresh of them has got.</summary>
    [HttpGet("sources/{name}")]
    public async Task<IActionResult> SourceFailures(string name, CancellationToken ct)
    {
        var series = await HealthSourceRecovery.Failing(db, name)
            .OrderBy(m => m.Series!.Title)
            .Select(m => new { m.Id, m.SeriesId, m.Series!.Title, Error = m.LastError, m.LastRefresh })
            .ToListAsync(ct);
        var progress = recovery.Progress(name);
        return Ok(new { series, refreshing = progress != null, done = progress?.Done ?? 0, total = progress?.Total ?? 0 });
    }

    [HttpPost("sources/{name}/refresh")]
    public IActionResult RefreshSource(string name)
    {
        if (sources.Find(name) is not { } source) return NotFound();
        recovery.Enqueue(source.Name);
        return Accepted(new { refreshing = true });
    }

    [HttpGet("files")]
    public async Task<IActionResult> Files([FromQuery] int page = 1, [FromQuery] string? search = null,
        [FromQuery] int? rootId = null, [FromQuery] string? kind = null, [FromQuery] string? state = null, CancellationToken ct = default)
    {
        var query = db.HealthFiles.Where(f => !f.Removed);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(f => f.RelativePath.Contains(search));
        if (rootId != null) query = query.Where(f => f.RootFolderId == rootId);
        if (kind != null || state != null) query = query.Where(f => db.HealthFindings.Any(i => i.FileId == f.Id && i.Version == f.Version && (kind == null || i.Kind == kind) && (state == null || i.State == state)));
        var total = await query.CountAsync(ct);
        var files = await query.AsNoTracking().OrderBy(f => f.RelativePath).Skip((Math.Max(page, 1) - 1) * 30).Take(30)
            .Select(f => new { f.Id, f.RelativePath, f.Version, f.RootFolderId, f.SeriesId, f.ChapterFileId, f.Size, f.ContentHash, f.Status, f.AnalyzedAt })
            .ToListAsync(ct);
        var ids = files.Select(f => f.Id).ToArray();
        var findings = await db.HealthFindings.AsNoTracking().Where(f => ids.Contains(f.FileId) && f.State != "resolved").ToListAsync(ct);
        return Ok(new { total, items = files.Select(f => new { f.Id, f.RelativePath, f.Version, f.RootFolderId, f.SeriesId, f.ChapterFileId, f.Size, f.ContentHash, f.Status, f.AnalyzedAt, findings = findings.Where(i => i.FileId == f.Id && i.Version == f.Version).Select(Rendered) }) });
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
        return Ok(new
        {
            file,
            analysis = RenderedAnalysis(HealthScanService.Analysis(file)),
            chapters,
            mappings,
            match = await matches.MatchAsync(file, ct),
            findings = (await db.HealthFindings.AsNoTracking().Where(f => f.FileId == id && f.Version == file.Version).ToListAsync(ct))
                .Select(Rendered),
        });
    }

    [HttpGet("files/{id:int}/pages/{page:int}")]
    public async Task<IActionResult> Preview(int id, int page, [FromQuery] string version, CancellationToken ct)
    {
        var file = await db.HealthFiles.FindAsync([id], ct);
        if (file == null || file.Removed) return NotFound();
        if (file.Version != version) return this.Conflict(localizer, "error.health.previewStale");
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { return this.Conflict(localizer, "error.health.previewUnavailable"); }
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
        db.HealthHistory.Add(ReviewHistory(finding, user.UserId, request.State));
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
        if (request.State is not ("open" or "acknowledged" or "ignored")) return this.Fail(localizer, "error.health.unknownFindingState");
        if (request.FileIds is not { Length: > 0 and <= 500 }) return this.Fail(localizer, "error.health.selectUpTo500");
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
            db.HealthHistory.Add(ReviewHistory(finding, user.UserId, request.State));
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
    public Task<IActionResult> Import(ImportRequest request, [FromServices] CbzLinkService cbz, CancellationToken ct) =>
        ConflictGuard($"{request.FileIds?.Length ?? 0} files", async () =>
    {
        if (request.FileIds is not { Length: > 0 and <= 500 }) return this.Fail(localizer, "error.health.selectUpTo500");
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
            db.HealthHistory.Add(new()
            {
                UserId = user.UserId,
                Kind = "import",
                MessageKey = "health.history.import",
                ParamsJson = JsonSerializer.Serialize(new
                {
                    files = files.Count,
                    series = owners.Count,
                    linked,
                    unrecognized,
                }),
            });
            await db.SaveChangesAsync(ct);
        }
        return Ok(new { files = files.Count, series = owners.Count, linked, unrecognized, orphans });
    });

    /// <param name="Verify">Read every byte rather than only the zip's index. A full read of
    /// whatever the scan covers, so it is never what a sweep of the whole library does.</param>
    public record ScanRequest(int? RootFolderId = null, int? SeriesId = null, int[]? FileIds = null, bool Force = false, bool Verify = false);
    [HttpPost("scans")]
    public async Task<IActionResult> Scan(ScanRequest request, CancellationToken ct)
    {
        if (request.FileIds?.Length > 500) return BadRequest();
        var scan = new HealthScan { RootFolderId = request.RootFolderId, SeriesId = request.SeriesId, FileIdsJson = JsonSerializer.Serialize(request.FileIds ?? []), Force = request.Force, Verify = request.Verify };
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
    public Task<IActionResult> Repair(FileReview request, CancellationToken ct) =>
        ConflictGuard($"file {request.FileId}", async () =>
    {
        try
        {
            return Ok(Rendered(await operations.RequestAsync(request.FileId, request.Version, request.SourceMappingId, user.UserId, ct)));
        }
        catch (PdfRepairUnsupportedException)
        {
            return this.Fail(localizer, "health.repair.pdfUnsupported");
        }
    });
    public record BulkDelete(FileReview[] Files, bool Confirmed);
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
    /// <para>
    /// Each entry carries the version the reviewer saw (the same shape a single-file review uses),
    /// so a file rescanned or replaced while the modal was open is refused rather than deleted: the
    /// server never substitutes its own current version for the one the caller reviewed.
    /// </para>
    /// </remarks>
    [HttpPost("deletions/bulk")]
    public async Task<IActionResult> DeleteBulk(BulkDelete request, CancellationToken ct)
    {
        if (!request.Confirmed) return this.Fail(localizer, "error.health.confirmationRequired");
        if (request.Files is not { Length: > 0 and <= 100 }) return this.Fail(localizer, "error.health.selectUpTo100");
        var ids = request.Files.Select(f => f.FileId).ToArray();
        var paths = await db.HealthFiles.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.RelativePath, ct);
        var deleted = 0;
        var failures = new List<object>();
        foreach (var item in request.Files)
        {
            try
            {
                var op = await operations.PreviewDeleteAsync(item.FileId, item.Version, user.UserId, ct);
                await operations.ApplyAsync(op.Id, item.Version, true, false, ct);
                deleted++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                if (ex is not HealthRefusedException)
                {
                    logger.LogWarning(ex, "Could not delete health file {FileId}", item.FileId);
                }

                failures.Add(new
                {
                    Id = item.FileId,
                    RelativePath = paths.GetValueOrDefault(item.FileId),
                    message = localizer.Get(ex is HealthRefusedException refused ? refused.Key : "error.health.operationFailed"),
                });
            }
        }
        db.HealthHistory.Add(new()
        {
            UserId = user.UserId,
            Kind = "delete",
            MessageKey = "health.history.bulkDelete",
            ParamsJson = JsonSerializer.Serialize(new { deleted, refused = failures.Count }),
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted, failures });
    }

    [HttpPost("deletions/preview")]
    public Task<IActionResult> DeletePreview(FileReview request, CancellationToken ct) =>
        ConflictGuard($"file {request.FileId}", async () =>
        Ok(Rendered(await operations.PreviewDeleteAsync(request.FileId, request.Version, user.UserId, ct))));
    [HttpPost("operations/{id:int}/apply")]
    public Task<IActionResult> Apply(int id, ApplyReview request, CancellationToken ct) =>
        ConflictGuard($"operation {id}", async () =>
    { await operations.ApplyAsync(id, request.Version, request.Confirmed, request.ResetPositions, ct); return Ok(new { applied = true }); });

    private const int OperationGroupPage = 30;
    private const int OperationGroupPreview = 3;
    private const int OperationGroupRowsMax = 500;

    /// <summary>
    /// Repair operations grouped by kind, status and error, newest group first, paged over groups.
    /// A bulk delete the filesystem refused is one group however many files it covered; each group
    /// carries its total and its newest few rows, and <c>operations/group</c> serves the rest.
    /// </summary>
    [HttpGet("operations")]
    public async Task<IActionResult> Operations([FromServices] IUserSettings userSettings, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var keys = Listed()
            .Select(o => new { o.Kind, o.Status, o.ErrorKey, o.ErrorParamsJson, o.Error });
        var groups = await Listed()
            .GroupBy(o => new { o.Kind, o.Status, o.ErrorKey, o.ErrorParamsJson, o.Error })
            .Select(g => new { g.Key.Kind, g.Key.Status, g.Key.ErrorKey, g.Key.ErrorParamsJson, g.Key.Error, Count = g.Count(), Newest = g.Max(o => o.Id) })
            .OrderByDescending(g => g.Newest)
            .Skip((Math.Max(1, page) - 1) * OperationGroupPage).Take(OperationGroupPage)
            .ToListAsync(ct);

        var previews = new List<List<HealthOperation>>(groups.Count);
        foreach (var g in groups)
        {
            var key = OperationGroupKey.From(g.Kind, g.Status, g.ErrorKey, g.ErrorParamsJson, g.Error);
            previews.Add(await (await key.ApplyAsync(Listed(), ct)).OrderByDescending(o => o.Id).Take(OperationGroupPreview).ToListAsync(ct));
        }

        var rendered = await RenderedOperationsAsync(previews.SelectMany(p => p).ToList(), userSettings, ct);
        var items = new List<object>(groups.Count);
        var offset = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            items.Add(new
            {
                Key = OperationGroupKey.From(g.Kind, g.Status, g.ErrorKey, g.ErrorParamsJson, g.Error).Encode(),
                g.Kind,
                g.Status,
                Error = RenderedError(g.ErrorKey, g.ErrorParamsJson, g.Error),
                g.Count,
                Rows = rendered.GetRange(offset, previews[i].Count),
            });
            offset += previews[i].Count;
        }

        return Ok(new { total = await keys.Distinct().CountAsync(ct), items });
    }

    /// <summary>
    /// What the Repairs list shows: nothing dismissed, and no failure on a file that has since been
    /// removed or changed. A retry validates against the version that failed, so such a failure can
    /// never run again and would only sit on the list.
    /// </summary>
    private IQueryable<HealthOperation> Listed() => db.HealthOperations.AsNoTracking()
        .Where(o => o.DismissedAt == null
            && !(o.Status == "failed" && db.HealthFiles.Any(f => f.Id == o.FileId && (f.Removed || f.Version != o.Version))));

    public record DismissGroup(string Key);

    /// <summary>Clears every operation in a finished group off the list.</summary>
    [HttpPost("operations/group/dismiss")]
    public async Task<IActionResult> DismissOperationGroup(DismissGroup request, CancellationToken ct)
    {
        var group = OperationGroupKey.Decode(request.Key);
        if (group is null) return BadRequest();
        if (!HealthOperationService.Terminal(group.Status)) return this.Conflict(localizer, "error.health.operationNotFinished");
        var query = await group.ApplyAsync(db.HealthOperations.Where(o => o.DismissedAt == null), ct);
        var now = DateTime.UtcNow;
        return Ok(new { dismissed = await query.ExecuteUpdateAsync(s => s.SetProperty(o => o.DismissedAt, now), ct) });
    }

    /// <summary>One group's rows, newest first: what "Show all" on a group loads.</summary>
    [HttpGet("operations/group")]
    public async Task<IActionResult> OperationGroup([FromServices] IUserSettings userSettings, [FromQuery] string key, [FromQuery] int take = OperationGroupPage, CancellationToken ct = default)
    {
        var group = OperationGroupKey.Decode(key);
        if (group is null) return BadRequest();
        var query = await group.ApplyAsync(Listed(), ct);
        var rows = await query.OrderByDescending(o => o.Id).Take(Math.Clamp(take, 1, OperationGroupRowsMax)).ToListAsync(ct);
        return Ok(new { total = await query.CountAsync(ct), items = await RenderedOperationsAsync(rows, userSettings, ct) });
    }

    private async Task<List<object>> RenderedOperationsAsync(List<HealthOperation> rows, IUserSettings userSettings, CancellationToken ct)
    {
        var fileIds = rows.Select(o => o.FileId).Distinct().ToList();
        var files = await db.HealthFiles.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.RelativePath, f.SeriesId, f.RootFolderId }).ToDictionaryAsync(f => f.Id, ct);
        // An archive with no ChapterFile has no SeriesId; name the series whose folder holds it,
        // the same containment rule HealthMatchService uses, so the row still gets a poster. Any
        // root will do when its own has no match: a root re-added under a new path keeps its
        // old rows. It only picks a poster, so a wrong guess costs nothing.
        var folders = files.Values.Any(f => f.SeriesId == null)
            ? await db.Series.AsNoTracking().Select(s => new { s.Id, s.RootFolderId, s.FolderName }).ToListAsync(ct)
            : [];
        var ownerOf = files.Values.ToDictionary(f => f.Id, f => f.SeriesId ?? folders
            .Where(s => HealthMatchService.Contains(s.FolderName, f.RelativePath))
            .OrderByDescending(s => s.RootFolderId == f.RootFolderId).ThenByDescending(s => s.FolderName.Length)
            .Select(s => (int?)s.Id).FirstOrDefault());
        var seriesIds = ownerOf.Values.OfType<int>().Distinct().ToList();
        var series = await db.Series.AsNoTracking().Where(s => seriesIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var titleLanguage = await userSettings.GetAsync(SettingKeys.UiTitleLanguage, ct);
        return rows.Select(op =>
        {
            var file = files.GetValueOrDefault(op.FileId);
            var owner = ownerOf.GetValueOrDefault(op.FileId) is { } id ? series.GetValueOrDefault(id) : null;
            return Rendered(op, file?.RelativePath, owner, titleLanguage);
        }).ToList();
    }

    /// <summary>
    /// What makes two operations the same problem. The key the client holds is these five fields,
    /// base64url encoded, so it stays the same while operations join or leave the group. A legacy
    /// <c>Error</c> longer than <see cref="ErrorInlineMax"/> travels as a short prefix plus its
    /// SHA-256: the prefix narrows the query and the hash picks the exact group, so the URL stays
    /// short and two long errors that open the same way never merge.
    /// </summary>
    private sealed record OperationGroupKey(string Kind, string Status, string? ErrorKey, string? ErrorParamsJson, string? Error, string? ErrorHash = null)
    {
        private const int ErrorInlineMax = 200;

        private static readonly JsonSerializerOptions WireOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private sealed record Wire(string K, string S, string? Ek, string? Ep, string? E, string? H);

        public static OperationGroupKey From(string kind, string status, string? errorKey, string? errorParamsJson, string? error)
        {
            if (error is not { Length: > ErrorInlineMax }) return new(kind, status, errorKey, errorParamsJson, error);
            var cut = char.IsHighSurrogate(error[ErrorInlineMax - 1]) ? ErrorInlineMax - 1 : ErrorInlineMax;
            return new(kind, status, errorKey, errorParamsJson, error[..cut], HashOf(error));
        }

        private static string HashOf(string error) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(error)));

        public string Encode() =>
            WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Wire(Kind, Status, ErrorKey, ErrorParamsJson, Error, ErrorHash), WireOptions));

        /// <summary>The group's operations. A long error is matched by prefix in SQL and then by hash in memory.</summary>
        public async Task<IQueryable<HealthOperation>> ApplyAsync(IQueryable<HealthOperation> source, CancellationToken ct)
        {
            source = source.Where(o => o.Kind == Kind && o.Status == Status && o.ErrorKey == ErrorKey && o.ErrorParamsJson == ErrorParamsJson);
            if (ErrorHash is null) return source.Where(o => o.Error == Error);
            var prefix = Error!;
            var candidates = await source.Where(o => o.Error != null && o.Error.StartsWith(prefix))
                .Select(o => new { o.Id, o.Error }).ToListAsync(ct);
            var ids = candidates.Where(c => HashOf(c.Error!) == ErrorHash).Select(c => c.Id).ToList();
            return source.Where(o => ids.Contains(o.Id));
        }

        public static OperationGroupKey? Decode(string? key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            try
            {
                var wire = JsonSerializer.Deserialize<Wire>(WebEncoders.Base64UrlDecode(key), WireOptions);
                if (wire is null || string.IsNullOrEmpty(wire.K) || string.IsNullOrEmpty(wire.S)) return null;
                if (wire.H is not null && string.IsNullOrEmpty(wire.E)) return null;
                return new(wire.K, wire.S, wire.Ek, wire.Ep, wire.E, wire.H);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return null;
            }
        }
    }

    [HttpGet("operations/{id:int}")]
    public async Task<IActionResult> Operation(int id, CancellationToken ct)
    {
        var op = await db.HealthOperations.FindAsync([id], ct);
        if (op == null) return NotFound();
        var file = await db.HealthFiles.FindAsync([op.FileId], ct);
        var chapters = await db.Chapters.Where(c => file != null && file.ChapterFileId != null && c.ChapterFileId == file.ChapterFileId).Select(c => new { c.Id, c.Title, c.Wanted }).ToListAsync(ct);
        var candidates = HealthOperationService.Candidates(op);
        return Ok(new
        {
            operation = Rendered(op),
            file,
            chapters,
            candidates = candidates.Select(c => new { c.ChapterId, c.RelativePath, c.Hash, Analysis = RenderedAnalysis(c.Analysis), c.FinalPath, c.SourceMappingId }),
            requiresReset = file != null && HealthOperationService.RequiresReset(file, candidates, chapters.Count),
            retryable = HealthOperationService.Retryable(op, file),
        });
    }

    [HttpPost("operations/{id:int}/dismiss")]
    public async Task<IActionResult> DismissOperation(int id, CancellationToken ct)
    {
        var op = await db.HealthOperations.FindAsync([id], ct);
        if (op == null) return NotFound();
        if (!HealthOperationService.Terminal(op.Status)) return this.Conflict(localizer, "error.health.operationNotFinished");
        op.DismissedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(Rendered(op));
    }

    [HttpPost("operations/{id:int}/retry")]
    public Task<IActionResult> RetryOperation(int id, CancellationToken ct) =>
        ConflictGuard($"operation {id}", async () =>
        {
            try { return Ok(Rendered(await operations.RetryAsync(id, user.UserId, ct))); }
            catch (PdfRepairUnsupportedException) { return this.Fail(localizer, "health.repair.pdfUnsupported"); }
        });
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { return this.Conflict(localizer, "error.health.candidatePreviewUnavailable"); }
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
                // Staged candidate archives (and any rollback copy) belong to this operation alone;
                // nothing else will clean them up once it is cancelled. Best-effort - a missing root
                // just leaves it for the startup sweep.
                try { await operations.RemoveStagingAsync(op, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            return Ok(Rendered(op));
        }
        finally { HealthOperationService.MutationGate.Release(); }
    }
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int page = 1, CancellationToken ct = default)
    {
        var rows = await db.HealthHistory.AsNoTracking().OrderByDescending(h => h.Id).Skip((Math.Max(1, page) - 1) * 30).Take(30).ToListAsync(ct);
        var checkOf = (string? json) => HealthMonitor.HealthParams(json).GetValueOrDefault("check") as string;
        var checkIds = rows.Where(r => r.Kind == "transition").Select(r => checkOf(r.ParamsJson)).OfType<string>().Distinct().ToList();
        var checks = await db.HealthChecks.AsNoTracking().Where(c => checkIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        // Rows from before history recorded the status a change left: the check's previous
        // transition ended in it. Transitions are rare, so a fixed look back covers years of them.
        var maxId = rows.Count == 0 ? 0 : rows[0].Id;
        var earlier = checkIds.Count == 0 ? [] : (await db.HealthHistory.AsNoTracking()
                .Where(h => h.Kind == "transition" && h.Id < maxId).OrderByDescending(h => h.Id).Take(1000)
                .Select(h => new { h.Id, h.ParamsJson }).ToListAsync(ct))
            .Select(h => (h.Id, Check: checkOf(h.ParamsJson), Status: HealthMonitor.HealthParams(h.ParamsJson).GetValueOrDefault("status") as string))
            .ToList();
        string? From(HealthHistory row) => row.Kind != "transition" || checkOf(row.ParamsJson) is not { } check ? null
            : earlier.FirstOrDefault(e => e.Id < row.Id && e.Check == check).Status;

        return Ok(new { total = await db.HealthHistory.CountAsync(ct), items = rows.Select(r => Rendered(r, checks, From(r))) });
    }
    private async Task<IActionResult> ConflictGuard(string target, Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (HealthRefusedException ex)
        { return this.Conflict(localizer, ex.Key); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "{Method} {Path} failed for {Target}", Request.Method, Request.Path, target);
            return this.Conflict(localizer, "error.health.operationFailed");
        }
    }

    private async Task<IActionResult> VerifiedPreview(string path, string? hash, PageFingerprint page, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (hash == null || Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != hash) return this.Conflict(localizer, "error.health.previewContentChanged");

        if (ComicFile.IsPdf(path))
        {
            if (!PdfReader.TryParsePageIndex(page.Name, out var index)) return NotFound();
            await using var rendered = await PdfReader.RenderPageAsync(path, index, PdfReader.FingerprintEdge, ct);
            var pdfBytes = rendered.ToArray();
            if (Convert.ToHexString(SHA256.HashData(pdfBytes)) != page.RawHash) return Conflict();
            Response.Headers.CacheControl = "no-store";
            return File(pdfBytes, "image/jpeg");
        }

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
