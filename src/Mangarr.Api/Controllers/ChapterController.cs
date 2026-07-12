using Mangarr.Api.Services;
using Mangarr.Core.Parsing;
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
        var rows = await db.Chapters
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

        // When a chapter's backing file is a volume/compilation CBZ, surface that
        // volume so the UI can show "Vol.x Ch.y" even for scrape-source chapters that
        // carry no volume metadata (parsing can't run inside the EF query, so it's
        // done here in memory over the materialized rows).
        var chapters = rows.Select(c => new
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
            c.HasFile,
            c.FilePath,
            FileVolume = VolumeFileLabel(c.FilePath)
        });

        return Ok(chapters);
    }

    /// <summary>The volume label ("3", "1-2") of a backing file when it is a volume compilation, else null.</summary>
    private static string? VolumeFileLabel(string? relativePath)
    {
        if (relativePath is null)
        {
            return null;
        }

        var parsed = ReleaseNameParser.ParseFileName(relativePath);
        if (!parsed.IsVolume)
        {
            return null;
        }

        return parsed.VolumeEnd is { } end && end != parsed.Volume
            ? $"{parsed.Volume}-{end}"
            : parsed.Volume!.Value.ToString();
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
