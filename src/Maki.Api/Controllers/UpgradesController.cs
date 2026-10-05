using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Parsing;
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
    /// <summary>The latest scan of one series asked for since startup; 204 when there is none.</summary>
    [HttpGet("scan/status")]
    public async Task<IActionResult> ScanStatus(
        [FromQuery] int seriesId, [FromServices] UpgradeScanTracker tracker, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        return tracker.Status(seriesId) is { } status ? Ok(status) : NoContent();
    }

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
            ScanRunning = UpgradeScanService.IsRunning,
            PendingProposals = await db.TorrentProposals.CountAsync(p => p.Status == TorrentProposalStatus.Pending, ct),
            LastVolumeSearchDate = await settings.GetAsync(SettingKeys.UpgradesLastVolumeSearchDate, ct)
        });
    }

    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpGet("proposals")]
    public async Task<IActionResult> Proposals(
        [FromQuery] int? seriesId, [FromQuery] string? status = "pending", CancellationToken ct = default)
    {
        var query = db.TorrentProposals.AsNoTracking();
        if (seriesId is { } only)
        {
            query = query.Where(p => p.SeriesId == only);
        }

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<TorrentProposalStatus>(status, ignoreCase: true, out var wanted) ||
                !Enum.IsDefined(wanted) || int.TryParse(status, out _))
            {
                return Ok(Array.Empty<TorrentProposalDto>());
            }

            query = query.Where(p => p.Status == wanted);
        }

        var rows = await query
            .OrderByDescending(p => p.CreatedAtUtc).ThenByDescending(p => p.Id)
            .Select(p => new { Proposal = p, SeriesTitle = db.Series.Where(s => s.Id == p.SeriesId).Select(s => s.Title).FirstOrDefault() })
            .ToListAsync(ct);
        return Ok(rows.Select(r => ProposalDto(r.Proposal, r.SeriesTitle)).ToList());
    }

    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("proposals/{id:int}/grab")]
    public async Task<IActionResult> GrabProposal(
        int id, [FromServices] TorrentUpgradeService torrents, [FromServices] ICurrentUser user, CancellationToken ct)
    {
        try
        {
            var (queueItemId, error) = await torrents.GrabProposalAsync(id, user.UserId, ct);
            return error switch
            {
                ProposalActionError.NotFound => this.NotFoundMessage(localizer, "error.upgrades.proposalNotFound"),
                ProposalActionError.Resolved => this.Conflict(localizer, "error.upgrades.proposalResolved"),
                _ => Ok(new { queueItemId })
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return this.Fail(localizer, "error.upgrades.grabFailed", new { detail = ex.Message });
        }
    }

    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("proposals/{id:int}/dismiss")]
    public async Task<IActionResult> DismissProposal(
        int id, [FromServices] TorrentUpgradeService torrents, [FromServices] ICurrentUser user, CancellationToken ct) =>
        await torrents.DismissAsync(id, user.UserId, ct) switch
        {
            ProposalActionError.NotFound => this.NotFoundMessage(localizer, "error.upgrades.proposalNotFound"),
            ProposalActionError.Resolved => this.Conflict(localizer, "error.upgrades.proposalResolved"),
            _ => NoContent()
        };

    /// <summary>
    /// With a series: searches it now, ignoring the weekly interval and the per-run cap. Without: queues
    /// the volume search job (admin only) and answers 202.
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("volume-search")]
    public async Task<IActionResult> VolumeSearch(
        [FromBody] VolumeSearchRequest? request, [FromServices] TorrentUpgradeService torrents,
        [FromServices] ISchedulerFactory schedulerFactory, [FromServices] ICurrentUser user, CancellationToken ct)
    {
        if (request?.SeriesId is not { } seriesId)
        {
            if (!user.Permissions.Grants(MakiPermission.Admin))
            {
                return Forbid();
            }

            if (UpgradeVolumeSearchJob.IsRunning)
            {
                return this.Conflict(localizer, "error.upgrades.scanRunning");
            }

            await UpgradeVolumeSearchJob.TriggerAsync(schedulerFactory, logger);
            return Accepted(new { started = true });
        }

        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        if (UpgradeVolumeSearchJob.IsRunning)
        {
            return this.Conflict(localizer, "error.upgrades.scanRunning");
        }

        var result = await torrents.SearchSeriesAsync(seriesId, ct, ignoreGlobalSwitch: true);
        return Ok(new SeriesVolumeSearchResultDto(result.Searched, result.ResultCount, result.Grabbed, result.ProposalId,
            result.Reason));
    }

    private static TorrentProposalDto ProposalDto(TorrentProposal p, string? seriesTitle)
    {
        var stored = Deserialize<StoredReleaseSpan>(p.SpanJson) ?? new StoredReleaseSpan(null, []);
        var tier = QualityTierResolver.Resolve(null, p.Title, p.Title, stored.Volumes is not null || stored.WholeSeries);
        return new TorrentProposalDto(
            p.Id, p.SeriesId, seriesTitle ?? string.Empty, p.Title, p.Indexer, p.SizeBytes,
            ReleaseSpanDto.From(stored.ToSpan(), stored.WholeSeries),
            Deserialize<List<string>>(p.ReasonsJson) ?? [], p.UpgradeCount, p.AlreadyMetCount, p.SkippedCount,
            p.MissingCount, p.UnknownCount, p.Score, QualityNames.Tier(tier), p.Status.ToString().ToLowerInvariant(),
            p.CreatedAtUtc, p.ResolvedAtUtc, p.QueueItemId);
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, QualitySnapshot.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// With a chapter: scans it now and answers with what happened. With a series: queues a scan of it
    /// and answers 202, since probing a whole series can take minutes. With neither: queues the
    /// library-wide scan (admin only) and answers 202.
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("scan")]
    public async Task<IActionResult> Scan(
        [FromBody] UpgradeScanRequest? request, [FromServices] UpgradeScanService scans,
        [FromServices] ISchedulerFactory schedulerFactory, [FromServices] ICurrentUser user,
        [FromServices] UpgradeScanTracker tracker, CancellationToken ct)
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
        else if (request.SeriesId is { } seriesId)
        {
            if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
            {
                return NotFound();
            }

            if (UpgradeScanService.IsRunning)
            {
                return this.Conflict(localizer, "error.upgrades.scanRunning");
            }

            tracker.Queued(seriesId);
            if (!await UpgradeScanJob.TriggerSeriesAsync(schedulerFactory, logger, seriesId))
            {
                tracker.Ended(seriesId, "failed");
            }

            return Accepted(new { started = true });
        }

        try
        {
            return Ok(UpgradeScanResultDto.From(await scans.ScanChapterAsync(request.ChapterId!.Value, user.UserId, ct)));
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
            UpgradeRevertError.FileChanged => this.Conflict(localizer, "error.upgrades.fileChangedSinceUpgrade"),
            UpgradeRevertError.DownloadInFlight => this.Conflict(localizer, "error.upgrades.revertDownloadInFlight"),
            _ => Ok((await RowsAsync([id], ct))[id])
        };
    }

    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("history/group/{groupId:guid}/revert")]
    public async Task<IActionResult> RevertGroup(
        Guid groupId, [FromServices] UpgradeRevertService reverts, [FromServices] ICurrentUser user,
        CancellationToken ct)
    {
        var (rows, error) = await reverts.RevertGroupAsync(groupId, user.UserId, ct);
        if (error == UpgradeRevertError.None)
        {
            var ids = rows.Select(r => r.Id).ToList();
            var dtos = await RowsAsync(ids, ct);
            return Ok(ids.Select(id => dtos[id]).ToList());
        }

        return error switch
        {
            UpgradeRevertError.NotFound => this.NotFoundMessage(localizer, "error.upgrades.historyNotFound"),
            UpgradeRevertError.AlreadyReverted => this.Conflict(localizer, "error.upgrades.alreadyReverted"),
            UpgradeRevertError.NotLatest => this.Conflict(localizer, "error.upgrades.notLatest"),
            UpgradeRevertError.TrashGone => this.Conflict(localizer, "error.upgrades.trashGone"),
            UpgradeRevertError.DownloadInFlight => this.Conflict(localizer, "error.upgrades.revertDownloadInFlight"),
            _ => this.Conflict(localizer, "error.upgrades.revertMoveFailed")
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
                ProfileName = db.UpgradeProfiles.Where(p => p.Id == h.ProfileId).Select(p => p.Name).FirstOrDefault(),
                GroupSize = h.GroupId == null ? 1 : db.UpgradeHistory.Count(g => g.GroupId == h.GroupId)
            })
            .ToListAsync(ct);

        var replacementIds = rows
            .Select(r => VolumeReplacementDetail.Parse(r.History.DetailJson)?.ReplacementFileId)
            .OfType<int>()
            .Distinct()
            .ToList();
        var replacementPaths = await db.ChapterFiles.AsNoTracking()
            .Where(f => replacementIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.RelativePath, ct);

        return rows.ToDictionary(r => r.History.Id, r =>
        {
            var h = r.History;
            var trashAvailable = h.RevertedAtUtc is null && h.TrashPath is { } trash && r.RootPath is { } root &&
                                 LibraryPaths.Resolve(root, trash) is { } path && System.IO.File.Exists(path);
            var detail = VolumeReplacementDetail.Parse(h.DetailJson);
            var fileName = detail is null
                ? Path.GetFileName(r.RelativePath ?? string.Empty)
                : Path.GetFileName(replacementPaths.GetValueOrDefault(detail.ReplacementFileId)
                                   ?? QualitySnapshot.Parse(h.AfterJson)?.ReleaseName ?? detail.RelativePath);
            return new UpgradeHistoryRowDto(
                h.Id,
                h.SeriesId,
                r.SeriesTitle ?? string.Empty,
                h.ChapterId,
                r.Chapter?.Number,
                r.Chapter?.Title,
                h.ChapterFileId,
                fileName,
                QualitySnapshotDto.From(QualitySnapshot.Parse(h.BeforeJson) ?? new QualitySnapshot()),
                QualitySnapshotDto.From(QualitySnapshot.Parse(h.AfterJson) ?? new QualitySnapshot()),
                r.ProfileName ?? string.Empty,
                h.TrashBytes,
                trashAvailable,
                h.CreatedAtUtc,
                h.RevertedAtUtc,
                h.GroupId?.ToString(),
                r.GroupSize);
        });
    }
}
