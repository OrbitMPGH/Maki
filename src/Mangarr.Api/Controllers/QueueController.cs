using Mangarr.Api.Dtos;
using Mangarr.Api.Services;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/queue")]
public class QueueController(MangarrDbContext db, DownloadQueueService queue) : ControllerBase
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
            .OrderByDescending(q => q.QueuedAt)
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
            return Conflict(new { error = "Only failed items can be retried" });
        }

        item.Status = QueueStatus.Queued;
        item.ErrorMessage = null;
        await db.SaveChangesAsync(ct);
        await queue.SignalAsync(item.Id, ct);
        return Ok();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var item = await db.DownloadQueue.FindAsync([id], ct);
        if (item is null)
        {
            return NotFound();
        }

        if (item.Status is QueueStatus.Queued or QueueStatus.Failed or QueueStatus.RateLimited)
        {
            db.DownloadQueue.Remove(item);
        }
        else
        {
            item.Status = QueueStatus.Cancelled;
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
