using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Quality;
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
    TorrentImportService importer,
    EventBroadcaster events,
    ISchedulerFactory schedulerFactory,
    ILogger<QueueController> logger)
    : ControllerBase
{
    /// <summary>
    /// The active queue, paginated like <see cref="History"/>. <c>Total</c> is the full count, so a
    /// caller can tell a full page from a truncated one — the old fixed <c>.Take(200)</c> dropped
    /// the rest silently and a big queue simply looked like exactly 200 items.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 200, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled);

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
                ? localizer.Get(outcome.ErrorKey, outcome.ErrorArgs)
                : outcome.Error;
            if (outcome.ErrorKey is not null) item.SetError(outcome.ErrorKey, outcome.ErrorArgs);
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

    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var item = await db.DownloadQueue.FindAsync([id], ct);
        if (item is null)
        {
            return NotFound();
        }

        queue.CancelWork(item.Id);

        if (item.Status is QueueStatus.Queued or QueueStatus.Failed or QueueStatus.RateLimited or QueueStatus.Resolving)
        {
            db.DownloadQueue.Remove(item);
        }
        else
        {
            item.Status = QueueStatus.Cancelled;
        }

        await db.SaveChangesAsync(ct);

        // The item will never report an outcome now, so let go of it — otherwise it holds its
        // series' download batch open and the batch's summary never fires.
        await batches.DiscardAsync(item.SeriesId, item.Id);
        return NoContent();
    }

    /// <summary>
    /// Removes every item from the active queue and stops any local work already in progress.
    /// In-flight items remain in history as cancelled.
    /// </summary>
    [Authorize(Policy = Policies.ManageDownloadQueue)]
    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        var items = await db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            queue.CancelWork(item.Id);

            if (item.Status is QueueStatus.Queued or QueueStatus.Failed or QueueStatus.RateLimited or QueueStatus.Resolving)
            {
                db.DownloadQueue.Remove(item);
            }
            else
            {
                item.Status = QueueStatus.Cancelled;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var item in items)
        {
            await batches.DiscardAsync(item.SeriesId, item.Id);
        }

        return Ok(new QueueClearDto(items.Count));
    }
}
