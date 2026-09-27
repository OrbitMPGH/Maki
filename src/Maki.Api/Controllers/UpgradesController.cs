using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Quality;
using Maki.Core.Paths;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Controllers;

/// <summary>
/// How the library's files measure up against their upgrade profiles, what the upgrader replaced, and
/// the scan and revert actions. Reads need only a signed-in caller and see the root folders they have
/// access to.
/// </summary>
[ApiController]
[Route("api/v1/upgrades")]
public class UpgradesController(
    UpgradeEvaluationService upgrades, MakiDbContext db, ILocalizer localizer, ILogger<UpgradesController> logger)
    : ControllerBase
{
    [HttpGet("cutoff-unmet")]
    public async Task<IActionResult> CutoffUnmet(
        [FromQuery] int? seriesId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = UpgradeEvaluationService.DefaultPageSize, CancellationToken ct = default) =>
        Ok(await upgrades.CutoffUnmetAsync(seriesId, page, pageSize, ct));

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromServices] UpgradeTrashService trash, [FromServices] IAppSettings settings, CancellationToken ct)
    {
        var summary = await upgrades.SummaryAsync(ct);
        var (bytes, files) = await trash.SizeAsync(ct);
        return Ok(summary with
        {
            TrashBytes = bytes,
            TrashFiles = files,
            LastScanDate = await settings.GetAsync(SettingKeys.UpgradesLastScanDate, ct),
            ScanRunning = UpgradeScanService.IsRunning
        });
    }

    /// <summary>
    /// With a chapter or a series: scans it now and answers with what happened. With neither: queues the
    /// library-wide scan (admin only) and answers 202 straight away, since that one can take minutes.
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("scan")]
    public async Task<IActionResult> Scan(
        [FromBody] UpgradeScanRequest? request, [FromServices] UpgradeScanService scans,
        [FromServices] ISchedulerFactory schedulerFactory, [FromServices] ICurrentUser user, CancellationToken ct)
    {
        if (request is { SeriesId: not null, ChapterId: not null })
        {
            return this.Fail(localizer, "error.upgrades.scanTargetAmbiguous");
        }

        if (request is null || (request.SeriesId is null && request.ChapterId is null))
        {
            if (!user.Permissions.Grants(MakiPermission.Admin))
            {
                return Forbid();
            }

            if (UpgradeScanService.IsRunning)
            {
                return this.Conflict(localizer, "error.upgrades.scanRunning");
            }

            await UpgradeScanJob.TriggerAsync(schedulerFactory, logger);
            return Accepted(new { started = true });
        }

        if (request.ChapterId is { } chapterId)
        {
            if (!await db.Chapters.AnyAsync(c => c.Id == chapterId && c.ChapterFileId != null, ct))
            {
                return this.NotFoundMessage(localizer, "error.upgrades.chapterHasNoFile");
            }
        }
        else if (!await db.Series.AnyAsync(s => s.Id == request.SeriesId, ct))
        {
            return NotFound();
        }

        try
        {
            var result = request.ChapterId is { } id
                ? await scans.ScanChapterAsync(id, user.UserId, ct)
                : await scans.ScanSeriesAsync(request.SeriesId!.Value, ct);
            return Ok(UpgradeScanResultDto.From(result));
        }
        catch (UpgradeScanBusyException)
        {
            return this.Conflict(localizer, "error.upgrades.scanRunning");
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(
        [FromQuery] int? seriesId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = UpgradeEvaluationService.DefaultPageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, UpgradeEvaluationService.MaxPageSize);
        var query = db.UpgradeHistory.AsNoTracking();
        if (seriesId is { } only)
        {
            query = query.Where(h => h.SeriesId == only);
        }

        var total = await query.CountAsync(ct);
        var ids = await query
            .OrderByDescending(h => h.CreatedAtUtc).ThenByDescending(h => h.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(h => h.Id)
            .ToListAsync(ct);
        var rows = await RowsAsync(ids, ct);
        return Ok(new UpgradeHistoryPageDto([.. ids.Select(id => rows[id])], total, page, pageSize));
    }

    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("history/{id:int}/revert")]
    public async Task<IActionResult> Revert(
        int id, [FromServices] UpgradeRevertService reverts, [FromServices] ICurrentUser user,
        CancellationToken ct)
    {
        var (_, error) = await reverts.RevertAsync(id, user.UserId, ct);
        return error switch
        {
            UpgradeRevertError.NotFound => this.NotFoundMessage(localizer, "error.upgrades.historyNotFound"),
            UpgradeRevertError.AlreadyReverted => this.Conflict(localizer, "error.upgrades.alreadyReverted"),
            UpgradeRevertError.NotLatest => this.Conflict(localizer, "error.upgrades.notLatest"),
            UpgradeRevertError.TrashGone => this.Conflict(localizer, "error.upgrades.trashGone"),
            UpgradeRevertError.MoveFailed => this.Conflict(localizer, "error.upgrades.revertMoveFailed"),
            _ => Ok((await RowsAsync([id], ct))[id])
        };
    }

    private async Task<Dictionary<int, UpgradeHistoryRowDto>> RowsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var rows = await db.UpgradeHistory.AsNoTracking()
            .Where(h => ids.Contains(h.Id))
            .Select(h => new
            {
                History = h,
                SeriesTitle = db.Series.Where(s => s.Id == h.SeriesId).Select(s => s.Title).FirstOrDefault(),
                RootPath = db.Series.Where(s => s.Id == h.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault(),
                Chapter = db.Chapters.Where(c => c.Id == h.ChapterId).Select(c => new { c.Number, c.Title }).FirstOrDefault(),
                RelativePath = db.ChapterFiles.Where(f => f.Id == h.ChapterFileId).Select(f => f.RelativePath).FirstOrDefault(),
                ProfileName = db.UpgradeProfiles.Where(p => p.Id == h.ProfileId).Select(p => p.Name).FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.History.Id, r =>
        {
            var h = r.History;
            var trashAvailable = h.RevertedAtUtc is null && h.TrashPath is { } trash && r.RootPath is { } root &&
                                 LibraryPaths.Resolve(root, trash) is { } path && System.IO.File.Exists(path);
            return new UpgradeHistoryRowDto(
                h.Id,
                h.SeriesId,
                r.SeriesTitle ?? string.Empty,
                h.ChapterId,
                r.Chapter?.Number,
                r.Chapter?.Title,
                h.ChapterFileId,
                Path.GetFileName(r.RelativePath ?? string.Empty),
                QualitySnapshotDto.From(QualitySnapshot.Parse(h.BeforeJson) ?? new QualitySnapshot()),
                QualitySnapshotDto.From(QualitySnapshot.Parse(h.AfterJson) ?? new QualitySnapshot()),
                r.ProfileName ?? string.Empty,
                h.TrashBytes,
                trashAvailable,
                h.CreatedAtUtc,
                h.RevertedAtUtc);
        });
    }
}
