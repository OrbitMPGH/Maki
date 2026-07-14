using Mangarr.Api.Services;
using Mangarr.Core.Entities;
using Mangarr.Core.Parsing;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

public record LinkChaptersRequest(int[] ChapterIds, string RelativePath);

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
    public static string? VolumeFileLabel(string? relativePath)
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

    /// <summary>
    /// Bulk-links chapters to a specific file in the series folder — for compilation CBZs
    /// or oddly-named files the automatic linker (<see cref="CbzLinkService"/>) couldn't
    /// match. Creates the backing <see cref="ChapterFile"/> record if the file was never
    /// imported (e.g. an "unrecognized" file surfaced by <c>GET /series/{id}/files</c>).
    /// </summary>
    [HttpPut("link")]
    public async Task<IActionResult> Link([FromBody] LinkChaptersRequest request, CancellationToken ct)
    {
        if (request.ChapterIds.Length == 0)
        {
            return BadRequest(new { error = "No chapters selected" });
        }

        var chapters = await db.Chapters.Where(c => request.ChapterIds.Contains(c.Id)).ToListAsync(ct);
        if (chapters.Count != request.ChapterIds.Length)
        {
            return NotFound();
        }

        var seriesId = chapters[0].SeriesId;
        if (chapters.Any(c => c.SeriesId != seriesId))
        {
            return BadRequest(new { error = "Chapters belong to different series" });
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series?.RootFolder is null)
        {
            return BadRequest(new { error = "Series has no root folder" });
        }

        var absPath = Path.Combine(series.RootFolder.Path, request.RelativePath);
        if (!System.IO.File.Exists(absPath))
        {
            return BadRequest(new { error = "File not found on disk" });
        }

        var file = await db.ChapterFiles
            .FirstOrDefaultAsync(f => f.SeriesId == seriesId && f.RelativePath == request.RelativePath, ct);
        if (file is null)
        {
            file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = request.RelativePath,
                Size = new FileInfo(absPath).Length,
                SourceName = "Manual",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            await db.SaveChangesAsync(ct);
        }

        foreach (var chapter in chapters)
        {
            chapter.ChapterFileId = file.Id;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { fileId = file.Id, linked = chapters.Count });
    }

    /// <summary>Clears the file link on the given chapters, leaving them missing/unlinked.</summary>
    [HttpPut("unlink")]
    public async Task<IActionResult> Unlink([FromBody] int[] chapterIds, CancellationToken ct)
    {
        var chapters = await db.Chapters.Where(c => chapterIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var chapter in chapters)
        {
            chapter.ChapterFileId = null;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { unlinked = chapters.Count });
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
