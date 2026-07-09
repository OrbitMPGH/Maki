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
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeCompleted = false, CancellationToken ct = default)
    {
        var query = db.DownloadQueue
            .Include(q => q.SourceMapping)
            .Include(q => q.Chapter)
            .Include(q => q.Series)
            .AsQueryable();

        if (!includeCompleted)
        {
            query = query.Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled);
        }

        var items = await query.OrderByDescending(q => q.QueuedAt).Take(100).ToListAsync(ct);

        return Ok(items
            .Where(q => q.Series != null)
            .Select(q => QueueItemDto.FromEntity(
                q, q.Chapter, q.Series!,
                q.SourceMapping?.SourceName ?? (q.Protocol == AcquisitionProtocol.Torrent ? "torrent" : "?"))));
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

        if (item.Status is QueueStatus.Queued or QueueStatus.Failed)
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
