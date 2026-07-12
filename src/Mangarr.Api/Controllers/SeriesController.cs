using Mangarr.Api.Dtos;
using Mangarr.Api.Services;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Core.Metadata;
using Mangarr.Core.Naming;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/series")]
public class SeriesController(
    MangarrDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    SourceMatchService sourceMatchService,
    ChapterSyncService chapterSyncService,
    CbzLinkService cbzLinkService,
    SeriesMetadataRefreshService metadataRefresh,
    DownloadQueueService downloadQueue,
    IAppSettings appSettings,
    KavitaScanService kavitaScans,
    ILogger<SeriesController> logger) : ControllerBase
{
    /// <summary>Re-pulls all metadata from the provider, including the poster image.</summary>
    [HttpPost("{id:int}/refreshmetadata")]
    public async Task<IActionResult> RefreshMetadata(int id, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (!await metadataRefresh.RefreshAsync(series, includeCover: true, ct))
        {
            return BadRequest(new { error = "Metadata lookup failed — series has no provider id or the provider returned nothing" });
        }

        await db.SaveChangesAsync(ct);
        if (series.RootFolder is { } rootFolder)
        {
            kavitaScans.QueuePush(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        }

        return Ok(SeriesDto.FromEntity(series));
    }

    /// <summary>Re-standardizes the ComicInfo.xml inside every CBZ the series owns.</summary>
    [HttpPost("{id:int}/updatecomicinfo")]
    public async Task<IActionResult> UpdateComicInfo(int id, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.RootFolder is null)
        {
            return BadRequest(new { error = "Series has no root folder" });
        }

        var (updated, total) = await cbzLinkService.UpdateComicInfoAsync(series, ct);
        return Ok(new { updated, total });
    }

    /// <summary>Queues downloads for every monitored chapter that has no file yet.</summary>
    [HttpPost("{id:int}/searchmissing")]
    public async Task<IActionResult> SearchMissing(int id, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id, ct))
        {
            return NotFound();
        }

        var missing = await db.Chapters
            .Where(c => c.SeriesId == id && c.Monitored && c.ChapterFileId == null)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var chapterId in missing)
        {
            try
            {
                if (await downloadQueue.EnqueueChapterAsync(chapterId, ct) != null)
                {
                    queued++;
                }
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message, queued });
            }
        }

        return Ok(new { queued });
    }

    [HttpPost("{id:int}/refresh")]
    public async Task<IActionResult> Refresh(int id, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id, ct))
        {
            return NotFound();
        }

        var newChapters = await chapterSyncService.SyncSeriesAsync(id, ct);
        return Ok(new { newChapters = newChapters.Count });
    }

    /// <summary>
    /// Reconciles the series folder with the database: refreshes chapters first
    /// (which also merges duplicates and backfills volume numbers), then adopts
    /// new CBZ files, relinks files that previously matched no chapter, and
    /// drops records for files deleted from disk.
    /// </summary>
    [HttpPost("{id:int}/rescan")]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.RootFolder is null)
        {
            return BadRequest(new { error = "Series has no root folder" });
        }

        try
        {
            await chapterSyncService.SyncSeriesAsync(id, ct);
        }
        catch (Exception ex)
        {
            // A dead source shouldn't block relinking files already on disk.
            logger.LogWarning(ex, "Chapter sync failed during rescan of series {Id}", id);
        }

        var result = await cbzLinkService.RescanSeriesAsync(series, ct);
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var series = await db.Series.OrderBy(s => s.SortTitle).ToListAsync(ct);
        // Total counts chapters the user actually cares about: monitored ones, plus any
        // already downloaded. An unmonitored chapter with no file (e.g. a skipped special)
        // is excluded so a fully-downloaded series reads 39/39, not 39/40.
        var chapterCounts = await db.Chapters
            .GroupBy(c => c.SeriesId)
            .Select(g => new
            {
                SeriesId = g.Key,
                Total = g.Count(c => c.Monitored || c.ChapterFileId != null),
                WithFile = g.Count(c => c.ChapterFileId != null),
            })
            .ToDictionaryAsync(x => x.SeriesId, ct);

        return Ok(series.Select(s =>
        {
            chapterCounts.TryGetValue(s.Id, out var counts);
            return SeriesDto.FromEntity(s, counts?.Total ?? 0, counts?.WithFile ?? 0);
        }));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        // See List(): unmonitored, un-downloaded chapters don't count toward the total.
        var total = await db.Chapters.CountAsync(
            c => c.SeriesId == id && (c.Monitored || c.ChapterFileId != null), ct);
        var withFile = await db.Chapters.CountAsync(c => c.SeriesId == id && c.ChapterFileId != null, ct);
        return Ok(SeriesDto.FromEntity(series, total, withFile));
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddSeriesRequest request, CancellationToken ct)
    {
        var rootFolder = await db.RootFolders.FindAsync([request.RootFolderId], ct);
        if (rootFolder is null)
        {
            return BadRequest(new { error = "Root folder not found" });
        }

        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(request.MetadataProviderId, ct);
        if (metadata is null)
        {
            return BadRequest(new { error = "Series not found on metadata provider" });
        }

        if (metadata.MangaBakaId is int existingId &&
            await db.Series.AnyAsync(s => s.MangaBakaId == existingId, ct))
        {
            return Conflict(new { error = "Series already exists in library" });
        }

        var series = new Series
        {
            Title = metadata.Title,
            SortTitle = SortTitleFor(metadata.Title),
            OriginalTitle = metadata.OriginalTitle,
            Status = metadata.Status,
            Overview = metadata.Description,
            Year = metadata.Year,
            Genres = [.. metadata.Genres],
            Tags = [.. metadata.Tags],
            MangaBakaId = metadata.MangaBakaId,
            AniListId = metadata.AniListId,
            MalId = metadata.MalId,
            MangaUpdatesId = metadata.MangaUpdatesId,
            MangaDexUuid = metadata.MangaDexUuid,
            Monitored = request.Monitored,
            MonitorNewItems = await DefaultedMonitorMode(
                Enum.TryParse<NewChapterMonitorMode>(request.MonitorNewItems, true, out var mode)
                    ? mode
                    : NewChapterMonitorMode.All, ct),
            RootFolderId = rootFolder.Id,
            FolderName = FileNameSanitizer.Sanitize(metadata.Title),
            TotalChapters = metadata.TotalChapters,
            TotalVolumes = metadata.TotalVolumes,
            AuthorStory = metadata.AuthorStory,
            AuthorArt = metadata.AuthorArt,
            Added = DateTime.UtcNow,
            LastMetadataRefresh = DateTime.UtcNow
        };

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);

        try
        {
            Directory.CreateDirectory(Path.Combine(rootFolder.Path, series.FolderName));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create series folder for {Title}", series.Title);
        }

        if (metadata.CoverUrl != null)
        {
            var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
            if (coverPath != null)
            {
                series.CoverPath = coverPath;
                await db.SaveChangesAsync(ct);
            }
        }

        // Link site sources by title match, then pull the initial chapter list.
        try
        {
            var mapped = await sourceMatchService.AutoMatchAsync(series, ct);
            if (mapped.Count > 0)
            {
                await chapterSyncService.SyncSeriesAsync(series.Id, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto source matching failed for {Title}", series.Title);
        }

        return CreatedAtAction(nameof(Get), new { id = series.Id }, SeriesDto.FromEntity(series));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFiles, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (deleteFiles && series.RootFolder != null)
        {
            var folder = Path.Combine(series.RootFolder.Path, series.FolderName);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        db.Series.Remove(series);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record MonitorModeRequest(string Mode);

    /// <summary>
    /// Applies a monitor mode (All / MainOnly / None) to every existing chapter and
    /// persists it as the mode for chapters that appear later.
    /// </summary>
    [HttpPost("{id:int}/monitormode")]
    public async Task<IActionResult> SetMonitorMode(int id, [FromBody] MonitorModeRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<NewChapterMonitorMode>(request.Mode, true, out var mode))
        {
            return BadRequest(new { error = $"Unknown mode: {request.Mode}" });
        }

        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        series.MonitorNewItems = mode;
        var chapters = await db.Chapters.Where(c => c.SeriesId == id).ToListAsync(ct);
        foreach (var chapter in chapters)
        {
            chapter.Monitored = Chapter.MonitoredUnder(mode, chapter.Number);
        }

        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            mode = mode.ToString(),
            monitored = chapters.Count(c => c.Monitored),
            total = chapters.Count
        });
    }

    /// <summary>The "unmonitor specials" setting turns a requested All into MainOnly.</summary>
    private async Task<NewChapterMonitorMode> DefaultedMonitorMode(NewChapterMonitorMode requested, CancellationToken ct) =>
        requested == NewChapterMonitorMode.All &&
        await appSettings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"
            ? NewChapterMonitorMode.MainOnly
            : requested;

    private static string SortTitleFor(string title)
    {
        var lowered = title.ToLowerInvariant();
        foreach (var article in (string[])["the ", "a ", "an "])
        {
            if (lowered.StartsWith(article, StringComparison.Ordinal))
            {
                return lowered[article.Length..];
            }
        }

        return lowered;
    }
}
