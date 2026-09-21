using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/queue")]
public class QueueController(
    ILocalizer localizer,
    MakiDbContext db,
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    TorrentImportService importer,
    EventBroadcaster events)
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
        var items = await query
            .Include(q => q.SourceMapping)
            .Include(q => q.Chapter)
            .Include(q => q.Series)
            .OrderBy(q => q.SortOrder)
            .ThenBy(q => q.QueuedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = items
            .Where(q => q.Series != null)
            .Select(q => QueueItemDto.FromEntity(
                q, q.Chapter, q.Series!,
                q.SourceMapping?.SourceName ?? (q.Protocol == AcquisitionProtocol.Torrent ? "torrent" : "?")))
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
        var items = await query
            .Include(q => q.SourceMapping)
            .Include(q => q.Chapter)
            .Include(q => q.Series)
            .OrderByDescending(q => q.CompletedAt ?? q.QueuedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = items
            .Where(q => q.Series != null)
            .Select(q => QueueItemDto.FromEntity(
                q, q.Chapter, q.Series!,
                q.SourceMapping?.SourceName ?? (q.Protocol == AcquisitionProtocol.Torrent ? "torrent" : "?")))
            .ToList();

        return Ok(new QueueHistoryDto(dtos, total, page, pageSize));
    }

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
        return Ok(await importer.PlanAsync(item, item.Series, contentPath, ct));
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

        if (item.Status != QueueStatus.AwaitingImport)
        {
            return this.Conflict(localizer, "error.queue.notAwaitingImport");
        }

        if (request.Mode == ImportDecision.Reject)
        {
            item.Status = QueueStatus.Cancelled;
            item.CompletedAt = DateTime.UtcNow;
            item.SetError("error.download.importRejected");
            await db.SaveChangesAsync(ct);
            await batches.DiscardAsync(item.SeriesId, item.Id);
            await Broadcast(item);
            return NoContent();
        }

        var mode = request.Mode == ImportDecision.Replace
            ? TorrentImportMode.Replace
            : TorrentImportMode.SkipExisting;

        item.Status = QueueStatus.Importing;
        await db.SaveChangesAsync(ct);
        await Broadcast(item);

        TorrentImportOutcome outcome;
        try
        {
            var contentPath = await importer.ResolveContentPathAsync(item, ct);
            outcome = await importer.ImportAsync(item, item.Series, contentPath, mode, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Back to AwaitingImport, not Failed. The guard at the top of this method is the only
            // way in, so a row left reading Importing could never be retried from here, and the
            // poll job skips it too, since parked items are the user's to settle. Restoring the
            // state it arrived in is what keeps a failed attempt retryable.
            item.Status = QueueStatus.AwaitingImport;
            item.SetRawError(ex.Message);
            await db.SaveChangesAsync(ct);
            await Broadcast(item);
            throw;
        }

        if (!outcome.Applied)
        {
            item.Status = QueueStatus.Failed;
            item.SetRawError(outcome.Error);
            await db.SaveChangesAsync(ct);
            await Broadcast(item);
            return Conflict(new { error = outcome.Error });
        }

        item.Status = QueueStatus.Completed;
        item.CompletedAt = DateTime.UtcNow;
        item.PagesDone = item.PagesTotal;

        // Saved before the rename: its active-download check re-queries this row, and an item still
        // reading as in-flight makes it refuse to name the files it just imported.
        await db.SaveChangesAsync(ct);
        await importer.ApplyNamingAsync(item.Series, outcome.ImportedPaths, ct);
        await Broadcast(item);

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
