using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/queue")]
public class QueueController(
    ILocalizer localizer,
    MakiDbContext db,
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    DownloadPauseService pauses,
    SourceRegistry sourceRegistry,
    TorrentImportService importer,
    EventBroadcaster events,
    ISchedulerFactory schedulerFactory,
    ILogger<QueueController> logger)
    : ControllerBase
{
    /// <summary>
    /// The active queue, paginated like <see cref="History"/>. <c>Total</c> is the full count, so a
    /// caller can tell a full page from a truncated one; the old fixed <c>.Take(200)</c> dropped
    /// the rest silently and a big queue simply looked like exactly 200 items. <c>seriesId</c>
    /// narrows it to one series, which is what a series page needs to see its own queued chapters.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 200, [FromQuery] int? seriesId = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled);
        if (seriesId is { } onlySeries) query = query.Where(q => q.SeriesId == onlySeries);

        var total = await query.CountAsync(ct);
        var rows = await Rows(query
                .OrderBy(q => q.SortOrder)
                .ThenBy(q => q.QueuedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(ct);

        var dtos = rows
            .Where(r => r.Series != null)
            .Select(r => QueueItemDto.FromEntity(r.Item, r.Chapter, r.Series!, r.SourceName))
            .ToList();

        return Ok(new QueueHistoryDto(dtos, total, page, pageSize));
    }

    /// <summary>Completed/cancelled downloads, paginated — the "always visible" history feed.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.DownloadQueue
            .Where(q => q.Status == QueueStatus.Completed || q.Status == QueueStatus.Cancelled);

        var total = await query.CountAsync(ct);
        // Id breaks ties so rows sharing a timestamp cannot repeat or vanish between pages.
        var rows = await Rows(query
                .OrderByDescending(q => q.CompletedAt ?? q.QueuedAt)
                .ThenByDescending(q => q.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(ct);
        var items = rows.Select(r => r.Item).ToList();

        var historyIds = items
            .Where(q => q.UpgradeInfoJson != null && !TorrentUpgradeInfo.IsTorrent(q.UpgradeInfoJson))
            .Select(q => UpgradeInfo.Parse(q.UpgradeInfoJson)?.HistoryId)
            .OfType<int>()
            .ToList();
        var upgrades = await UpgradeHistoryStates.LoadAsync(db, historyIds, ct);
        var groupIds = items
            .Select(q => TorrentUpgradeInfo.Parse(q.UpgradeInfoJson)?.HistoryGroupId)
            .OfType<Guid>()
            .ToList();
        var groups = await UpgradeHistoryStates.LoadGroupsAsync(db, groupIds, ct);

        var dtos = rows
            .Where(r => r.Series != null)
            .Select(r => QueueItemDto.FromEntity(
                r.Item, r.Chapter, r.Series!, r.SourceName,
                TorrentUpgradeInfo.Parse(r.Item.UpgradeInfoJson) is { } torrent
                    ? torrent.HistoryGroupId is { } groupId ? groups.GetValueOrDefault(groupId) : null
                    : UpgradeInfo.Parse(r.Item.UpgradeInfoJson)?.HistoryId is { } historyId ? upgrades.GetValueOrDefault(historyId) : null))
            .ToList();

        return Ok(new QueueHistoryDto(dtos, total, page, pageSize));
    }

    /// <summary>
    /// Per-status counts for the shell's Activity badge, which polls every few seconds and needs two
    /// numbers, not a page of rows. Counts the whole queue, where the list endpoint is paged.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var counts = await db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled)
            .GroupBy(q => q.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        return Ok(new QueueSummaryDto(
            Active: counts.Where(kv => kv.Key is not (QueueStatus.Failed or QueueStatus.AwaitingImport)).Sum(kv => kv.Value),
            AwaitingImport: counts.GetValueOrDefault(QueueStatus.AwaitingImport),
            Failed: counts.GetValueOrDefault(QueueStatus.Failed)));
    }

    /// <summary>What is paused right now. Torrents already handed to qBittorrent are not part of it.</summary>
    [HttpGet("pause")]
    public async Task<IActionResult> GetPause(CancellationToken ct) =>
        Ok(QueuePauseDto.From(await pauses.ActiveAsync(ct)));

    /// <summary>
    /// Stops workers claiming new scraper downloads, for every source or one, until resumed or until
    /// <c>ResumeAt</c>. Items already downloading finish, and no row changes status.
    /// </summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpPut("pause")]
    public async Task<IActionResult> Pause([FromBody] QueuePauseRequestDto request, CancellationToken ct)
    {
        string? source = null;
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            source = sourceRegistry.Find(request.Source)?.Name;
            if (source is null)
            {
                return this.Fail(localizer, "error.queue.pauseUnknownSource");
            }
        }

        DateTime? until = request.ResumeAt is { } at
            ? at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime()
            : null;
        if (until <= DateTime.UtcNow)
        {
            return this.Fail(localizer, "error.queue.pauseResumeInPast");
        }

        return Ok(await ChangePauseAsync(await pauses.PauseAsync(source, until, ct), ct));
    }

    /// <summary>Lifts the pause on one source, or the global one when <c>source</c> is omitted.</summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpDelete("pause")]
    public async Task<IActionResult> Resume([FromQuery] string? source, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(source) ? null : source;
        return Ok(await ChangePauseAsync(await pauses.ResumeAsync(name, ct), ct));
    }

    private async Task<QueuePauseDto> ChangePauseAsync(DownloadPauseState state, CancellationToken ct)
    {
        var dto = QueuePauseDto.From(state);
        await events.QueuePauseChanged(dto);
        await queue.WakeWorkersAsync(ct);
        return dto;
    }

    private sealed record QueueRow(DownloadQueueItem Item, Chapter? Chapter, Series? Series, string SourceName);

    /// <summary>
    /// The columns a <see cref="QueueItemDto"/> is built from, untracked. Including the navigations
    /// instead loaded every Series column (and its JSON converters) for a title.
    /// </summary>
    private static IQueryable<QueueRow> Rows(IQueryable<DownloadQueueItem> query) =>
        query.AsNoTracking().Select(q => new QueueRow(
            q,
            q.Chapter == null
                ? null
                : new Chapter
                {
                    Id = q.Chapter.Id,
                    Number = q.Chapter.Number,
                    Volume = q.Chapter.Volume,
                    Title = q.Chapter.Title,
                    IsOneShot = q.Chapter.IsOneShot,
                },
            q.Series == null ? null : new Series { Id = q.Series.Id, Title = q.Series.Title },
            q.SourceMapping != null
                ? q.SourceMapping.SourceName
                : q.Protocol == AcquisitionProtocol.Torrent ? "torrent" : "?"));

    /// <summary>
    /// Sets the manual dispatch order for the active queue. <c>OrderedIds</c> is the full list of active
    /// item ids in the caller's desired order (as dragged in the Activity page) — items are assigned
    /// their index as <see cref="DownloadQueueItem.SortOrder"/>. Takes effect on the very
    /// next worker dispatch, no restart needed.
    /// </summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpPut("reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderQueueDto request, CancellationToken ct)
    {
        await queue.ReorderAsync(request.OrderedIds, ct);
        return NoContent();
    }

    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpPost("{id:int}/retry")]
    public async Task<IActionResult> Retry(int id, CancellationToken ct)
    {
        var item = await db.DownloadQueue.FindAsync([id], ct);
        if (item is null)
        {
            return NotFound();
        }

        if (item.Status != QueueStatus.Failed)
        {
            return this.Conflict(localizer, "error.queue.onlyFailedCanRetry");
        }

        // The chapter was queued again after this row failed, and only one row per chapter may be active.
        if (item.ChapterId is { } retryChapterId &&
            await db.DownloadQueue.AnyAsync(q => q.ActiveChapterId == retryChapterId, ct))
        {
            return this.Conflict(localizer, "error.chapter.alreadyQueued");
        }

        // Scraper item that never had a mapping resolved (e.g. it failed before
        // ResolveAndActivateAsync could set one) — ClaimNextAsync requires every Queued/RateLimited
        // scraper item to have one, so send it back through resolution instead of straight to
        // Queued. Torrent items never carry a SourceMappingId, so they're unaffected.
        if (item.Protocol == AcquisitionProtocol.Scraper && item.SourceMappingId is null)
        {
            if (item.ChapterId is not { } unresolvedChapterId)
            {
                return this.Conflict(localizer, "error.queue.noChapterToResolve");
            }

            item.Status = QueueStatus.Resolving;
            item.ClearError();
            item.NextAttempt = null;
            await db.SaveChangesAsync(ct);
            _ = queue.ResolveAndActivateAsync(item.Id, unresolvedChapterId, CancellationToken.None);
            return NoContent();
        }

        // If this item's tracker is already cooling down from something else, land it in
        // RateLimited straight away instead of a "Queued" that never explains why it isn't moving.
        var sourceName = item.SourceMappingId is { } mappingId
            ? await db.SourceMappings.Where(m => m.Id == mappingId).Select(m => m.SourceName).FirstOrDefaultAsync(ct)
            : null;
        var cooldownUntil = sourceName is not null ? queue.CooldownUntil(sourceName) : null;

        item.Status = cooldownUntil is null ? QueueStatus.Queued : QueueStatus.RateLimited;
        item.NextAttempt = cooldownUntil;
        if (cooldownUntil is null)
        {
            item.ClearError();
        }
        else
        {
            item.SetError("error.download.rateLimited", new { source = sourceName });
        }
        await db.SaveChangesAsync(ct);
        await queue.SignalAsync(item.Id, ct);
        return NoContent();
    }

    /// <summary>
    /// What importing a finished torrent would do to the library: per downloaded file, the chapters
    /// it covers, which of those are missing today, and which existing files it would leave backing
    /// nothing. Read by the activity list's import review.
    /// </summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpGet("{id:int}/import-plan")]
    public async Task<IActionResult> ImportPlan(int id, CancellationToken ct)
    {
        var item = await db.DownloadQueue
            .Include(q => q.Series)
            .FirstOrDefaultAsync(q => q.Id == id, ct);
        if (item?.Series is null)
        {
            return NotFound();
        }

        if (item.Protocol != AcquisitionProtocol.Torrent)
        {
            return this.Conflict(localizer, "error.queue.onlyTorrentImportable");
        }

        var contentPath = await importer.ResolveContentPathAsync(item, ct);
        var plan = await importer.PlanAsync(item, item.Series, contentPath, ct);
        return Ok(new
        {
            plan.QueueItemId,
            plan.SeriesId,
            plan.SeriesTitle,
            plan.ReleaseName,
            plan.Files,
            error = plan.ErrorKey is not null ? localizer.Get(plan.ErrorKey, plan.ErrorArgs) : null,
            plan.HasConflicts,
            plan.NewChapterCount,
            plan.ReplacedFileCount,
            plan.IsUpgrade,
            plan.SuggestedSkips
        });
    }

    /// <summary>
    /// Settles a download parked as <see cref="QueueStatus.AwaitingImport"/>: import everything and
    /// delete what it supersedes, import only the chapters the library is missing, or reject the
    /// download and leave the library alone. Nothing else may advance such an item — the whole
    /// point of parking it is that a person decides.
    /// </summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpPost("{id:int}/import")]
    public async Task<IActionResult> Import(int id, [FromBody] ImportDecisionDto request, CancellationToken ct)
    {
        var item = await db.DownloadQueue
            .Include(q => q.Series)!.ThenInclude(s => s!.RootFolder)
            .FirstOrDefaultAsync(q => q.Id == id, ct);
        if (item?.Series is null)
        {
            return NotFound();
        }

        if (item.Status != QueueStatus.AwaitingImport || !TorrentImportService.TryBeginManualImport(item.Id))
        {
            return this.Conflict(localizer, "error.queue.notAwaitingImport");
        }

        try
        {
            // Conditional, so a second click or the poll job cannot take the same parked row: only
            // one caller moves it out of AwaitingImport.
            var claimed = await db.DownloadQueue
                .Where(q => q.Id == id && q.Status == QueueStatus.AwaitingImport)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.Importing), ct);
            if (claimed == 0)
            {
                return this.Conflict(localizer, "error.queue.notAwaitingImport");
            }

            // The tracked copy has to agree with the row, or restoring AwaitingImport below would read
            // as no change and never be written.
            db.Entry(item).Property(q => q.Status).OriginalValue = QueueStatus.Importing;
            item.Status = QueueStatus.Importing;
            return await SettleImportAsync(item, request, ct);
        }
        finally
        {
            TorrentImportService.EndManualImport(item.Id);
        }
    }

    private async Task<IActionResult> SettleImportAsync(
        DownloadQueueItem item, ImportDecisionDto request, CancellationToken ct)
    {
        if (request.Mode == ImportDecision.Reject)
        {
            item.Status = QueueStatus.Cancelled;
            item.CompletedAt = DateTime.UtcNow;
            item.SetError("error.download.importRejected");
            if (TorrentUpgradeInfo.Parse(item.UpgradeInfoJson) is { } rejected)
            {
                rejected.Outcome = TorrentUpgradeOutcomes.Rejected;
                item.UpgradeInfoJson = rejected.Serialize();
            }

            await db.SaveChangesAsync(ct);
            await batches.DiscardAsync(item.SeriesId, item.Id);
            await Broadcast(item);
            return NoContent();
        }

        var mode = request.Mode == ImportDecision.Replace
            ? TorrentImportMode.Replace
            : TorrentImportMode.SkipExisting;

        await Broadcast(item);

        TorrentImportOutcome outcome;
        try
        {
            var contentPath = await importer.ResolveContentPathAsync(item, ct);
            var skipFiles = request.SkipFiles is { Count: > 0 } skip ? skip.ToHashSet(StringComparer.Ordinal) : null;
            outcome = await importer.ImportAsync(item, item.Series!, contentPath, mode, ct, skipFiles: skipFiles);
        }
        catch (Exception ex)
        {
            // Back to AwaitingImport, not Failed. The guard at the top of this method is the only
            // way in, so a row left reading Importing could never be retried from here, and the
            // poll job skips it too, since parked items are the user's to settle. Restoring the
            // state it arrived in is what keeps a failed attempt retryable. That holds for a dropped
            // request too, whose token is already cancelled, hence the uncancellable save.
            item.Status = QueueStatus.AwaitingImport;
            item.SetRawError(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            await Broadcast(item);
            throw;
        }

        if (outcome.ErrorKey == TorrentImportService.SeriesChangedKey)
        {
            // Deleted: the row cascaded away with the series. Moved: still the user's to settle.
            if (await db.DownloadQueue.AnyAsync(q => q.Id == item.Id, ct))
            {
                item.Status = QueueStatus.AwaitingImport;
                item.SetError(outcome.ErrorKey);
                await db.SaveChangesAsync(ct);
                await Broadcast(item);
            }

            return this.Conflict(localizer, outcome.ErrorKey);
        }

        if (!outcome.Applied)
        {
            item.Status = QueueStatus.Failed;
            var rendered = outcome.ErrorKey is not null
                ? localizer.Get(outcome.ErrorKey, outcome.ErrorArgs) + (outcome.Error is null ? "" : $": {outcome.Error}")
                : outcome.Error;
            if (outcome.ErrorKey is not null) item.SetError(outcome.ErrorKey, outcome.ErrorArgs, outcome.Error);
            else item.SetRawError(outcome.Error);
            await db.SaveChangesAsync(ct);
            await Broadcast(item);
            return Conflict(new { error = rendered });
        }

        item.Status = QueueStatus.Completed;
        item.CompletedAt = DateTime.UtcNow;
        item.PagesDone = item.PagesTotal;
        item.ClearError();

        // Saved before the rename: its active-download check re-queries this row, and an item still
        // reading as in-flight makes it refuse to name the files it just imported.
        await db.SaveChangesAsync(ct);
        await importer.ApplyNamingAsync(item.Series!, outcome.ImportedPaths, ct);
        await Broadcast(item);
        if (outcome.Imported > 0)
        {
            await ChapterFileMeasureJob.TriggerAsync(schedulerFactory, logger);
        }

        return Ok(new ImportDecisionResultDto(
            outcome.Imported, outcome.Linked, outcome.Skipped, outcome.Deleted));
    }

    private Task Broadcast(DownloadQueueItem item) =>
        events.QueueUpdated(QueueItemDto.FromEntity(item, chapter: null, item.Series!, "torrent"));

    /// <param name="blockRelease">
    /// For a torrent row: also record the release as turned down, so the volume search does not grab it
    /// again. Removing the row otherwise leaves no trace of it.
    /// </param>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(
        int id, CancellationToken ct = default, [FromQuery] bool blockRelease = true, [FromServices] ICurrentUser? user = null)
    {
        var row = await db.DownloadQueue.AsNoTracking()
            .Where(q => q.Id == id)
            .Select(q => new { q.SeriesId, q.Protocol, q.ReleaseInfoJson })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return NotFound();
        }

        if (await RemoveOrCancelAsync(id, ct))
        {
            if (blockRelease && row.Protocol == AcquisitionProtocol.Torrent)
            {
                await BlockReleasesAsync([(row.SeriesId, row.ReleaseInfoJson)], user, ct);
            }

            // The item will never report an outcome now, so let go of it, otherwise it holds its
            // series' download batch open and the batch's summary never fires.
            await batches.DiscardAsync(row.SeriesId, id);
        }

        return NoContent();
    }

    private async Task BlockReleasesAsync(
        IEnumerable<(int SeriesId, string? ReleaseInfoJson)> releases, ICurrentUser? user, CancellationToken ct)
    {
        int? userId = user is { IsAuthenticated: true } ? user.UserId : null;
        var now = DateTime.UtcNow;
        foreach (var (seriesId, json) in releases)
        {
            await TorrentUpgradeService.DeclineAsync(db, seriesId, json, userId, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Failed rows grouped by failure key and source, for choosing what a bulk retry or remove acts on.
    /// Counts the whole queue, where the list endpoint is paged.
    /// </summary>
    [HttpGet("failed")]
    public async Task<IActionResult> Failed(CancellationToken ct)
    {
        var groups = await db.DownloadQueue
            .Where(q => q.Status == QueueStatus.Failed)
            .GroupBy(q => new
            {
                q.ErrorKey,
                Source = q.SourceMapping != null
                    ? q.SourceMapping.SourceName
                    : q.Protocol == AcquisitionProtocol.Torrent ? DownloadQueueService.TorrentSourceName : DownloadQueueService.UnknownSourceName
            })
            .Select(g => new QueueFailureGroupDto(g.Key.ErrorKey, g.Key.Source, g.Count()))
            .ToListAsync(ct);
        return Ok(groups.OrderByDescending(g => g.Count).ThenBy(g => g.SourceName, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Retries every Failed scraper item, or the ones matching a failure key and/or a source. Like the
    /// single retry it ignores the automatic attempt cap and the backoff.
    /// </summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpPost("retry-failed")]
    public async Task<IActionResult> RetryFailed([FromBody] QueueFailedActionDto? request, CancellationToken ct)
    {
        var retried = await queue.RetryFailedAsync(request?.Reason, request?.Source, ct);
        return Ok(new QueueBulkResultDto(retried));
    }

    /// <summary>Removes every Failed item, or the ones matching a failure key and/or a source.</summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpPost("remove-failed")]
    public async Task<IActionResult> RemoveFailed(
        [FromBody] QueueFailedActionDto? request, [FromServices] ICurrentUser? user, CancellationToken ct)
    {
        var rows = await DownloadQueueService.FailedWhere(db.DownloadQueue.AsNoTracking(), request?.Reason, request?.Source)
            .Select(q => new { q.Id, q.SeriesId, q.Protocol, q.ReleaseInfoJson })
            .ToListAsync(ct);

        var removed = new List<(int Id, int SeriesId, string? ReleaseInfoJson, AcquisitionProtocol Protocol)>();
        foreach (var chunk in rows.Chunk(500))
        {
            var ids = chunk.Select(r => r.Id).ToList();
            // Conditional on Failed: a row retried or completed since the read is not this call's to delete.
            await db.DownloadQueue.Where(q => ids.Contains(q.Id) && q.Status == QueueStatus.Failed).ExecuteDeleteAsync(ct);
            var remaining = (await db.DownloadQueue.AsNoTracking()
                    .Where(q => ids.Contains(q.Id))
                    .Select(q => q.Id)
                    .ToListAsync(ct))
                .ToHashSet();
            removed.AddRange(chunk
                .Where(r => !remaining.Contains(r.Id))
                .Select(r => (r.Id, r.SeriesId, r.ReleaseInfoJson, r.Protocol)));
        }

        if (request?.BlockReleases ?? true)
        {
            await BlockReleasesAsync(
                removed.Where(r => r.Protocol == AcquisitionProtocol.Torrent).Select(r => (r.SeriesId, r.ReleaseInfoJson)),
                user, ct);
        }

        foreach (var row in removed)
        {
            await batches.DiscardAsync(row.SeriesId, row.Id);
        }

        return Ok(new QueueBulkResultDto(removed.Count));
    }

    /// <summary>
    /// Removes a pending row or cancels an in-flight one, but only while it is still in the status it
    /// was read in. A worker finishing the row in between must keep its Completed (the file is
    /// already in the library), and a worker that claimed a Queued row must not find it deleted under
    /// it. A lost race re-reads and tries again; a row that has settled by itself is left as it is.
    /// </summary>
    /// <returns>True when this call removed or cancelled the row.</returns>
    private async Task<bool> RemoveOrCancelAsync(int id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = await db.DownloadQueue.AsNoTracking()
                .Where(q => q.Id == id)
                .Select(q => (QueueStatus?)q.Status)
                .FirstOrDefaultAsync(ct);
            if (status is null or QueueStatus.Completed or QueueStatus.Cancelled)
            {
                return false;
            }

            queue.CancelWork(id);

            var row = db.DownloadQueue.Where(q => q.Id == id && q.Status == status);
            var affected = status is QueueStatus.Queued or QueueStatus.Failed or QueueStatus.RateLimited or QueueStatus.Resolving
                ? await row.ExecuteDeleteAsync(ct)
                : await row.ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.Cancelled), ct);
            if (affected > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes every item from the active queue and stops any local work already in progress.
    /// In-flight items remain in history as cancelled.
    /// </summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        var items = await db.DownloadQueue.AsNoTracking()
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled)
            .Select(q => new { q.Id, q.SeriesId })
            .ToListAsync(ct);

        // Two statements per chunk instead of three queries per row. Each is conditional on the status,
        // so a worker that completed or claimed a row meanwhile is not overwritten; a row that moved
        // from pending to in-flight between the statements is picked up by the next pass.
        var pending = new[] { QueueStatus.Queued, QueueStatus.Failed, QueueStatus.RateLimited, QueueStatus.Resolving };
        var terminal = new[] { QueueStatus.Completed, QueueStatus.Cancelled };
        var ids = items.Select(i => i.Id).ToList();
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = 0;
            foreach (var chunk in ids.Chunk(500))
            {
                foreach (var id in chunk)
                {
                    queue.CancelWork(id);
                }

                var rows = db.DownloadQueue.Where(q => chunk.Contains(q.Id));
                changed += await rows.Where(q => pending.Contains(q.Status)).ExecuteDeleteAsync(ct);
                changed += await rows
                    .Where(q => !pending.Contains(q.Status) && !terminal.Contains(q.Status))
                    .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.Cancelled), ct);
            }

            if (changed == 0)
            {
                break;
            }
        }

        var statuses = new Dictionary<int, QueueStatus>();
        foreach (var chunk in ids.Chunk(500))
        {
            foreach (var row in await db.DownloadQueue.AsNoTracking()
                         .Where(q => chunk.Contains(q.Id))
                         .Select(q => new { q.Id, q.Status })
                         .ToListAsync(ct))
            {
                statuses[row.Id] = row.Status;
            }
        }

        var cleared = 0;
        foreach (var item in items)
        {
            if (statuses.TryGetValue(item.Id, out var status) && status != QueueStatus.Cancelled)
            {
                continue;
            }

            cleared++;
            await batches.DiscardAsync(item.SeriesId, item.Id);
        }

        return Ok(new QueueClearDto(cleared));
    }
}
