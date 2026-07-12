using Mangarr.Api.Services;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/chapter")]
public class ChapterController(MangarrDbContext db, DownloadQueueService queue) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int seriesId, CancellationToken ct)
    {
        var chapters = await db.Chapters
            .Where(c => c.SeriesId == seriesId)
            .Include(c => c.ChapterFile)
            .OrderBy(c => c.Number == null ? 1 : 0).ThenBy(c => c.Number).ThenBy(c => c.Volume)
            .Select(c => new
            {
                c.Id,
                c.SeriesId,
                c.Number,
                c.NumberRaw,
                c.Volume,
                c.Title,
                c.IsOneShot,
                c.Language,
                c.ReleaseDate,
                c.Monitored,
                HasFile = c.ChapterFileId != null,
                FilePath = c.ChapterFile != null ? c.ChapterFile.RelativePath : null
            })
            .ToListAsync(ct);

        return Ok(chapters);
    }

    [HttpPut("{id:int}/monitor")]
    public async Task<IActionResult> SetMonitored(int id, [FromQuery] bool monitored, CancellationToken ct)
    {
        var chapter = await db.Chapters.FindAsync([id], ct);
        if (chapter is null)
        {
            return NotFound();
        }

        chapter.Monitored = monitored;
        await db.SaveChangesAsync(ct);
        return Ok(new { chapter.Id, chapter.Monitored });
    }

    [HttpPost("{id:int}/search")]
    public async Task<IActionResult> Search(int id, CancellationToken ct)
    {
        try
        {
            var item = await queue.EnqueueChapterAsync(id, ct);
            return item is null
                ? Conflict(new { error = "Chapter is already queued" })
                : Ok(new { queueItemId = item.Id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
