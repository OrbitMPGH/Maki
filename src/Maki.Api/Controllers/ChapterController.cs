using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Quality;
using Maki.Core.Reading;
using Maki.Core.Sources;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

public record LinkChaptersRequest(int[] ChapterIds, string RelativePath);

public record SetChaptersWantedRequest(int[] ChapterIds, bool Wanted);

public record DownloadChaptersRequest(int[] ChapterIds);

public record DownloadChapterFromRequest(int SourceMappingId);

public record RedownloadRequest(int SeriesId, string SourceName);

[ApiController]
[Route("api/v1/chapter")]
public class ChapterController(
    ILocalizer localizer,
    MakiDbContext db,
    DownloadQueueService queue,
    StatsEventService stats,
    ReaderArchiveCache archives,
    SourceRegistry sourceRegistry,
    SourceChapterListCache chapterLists,
    DownloadBatchNotifier downloadBatches,
    ICurrentUser currentUser,
    ILogger<ChapterController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int seriesId, [FromServices] UpgradeEvaluationService upgrades, CancellationToken ct)
    {
        var rows = await db.Chapters
            .Where(c => c.SeriesId == seriesId)
            .Include(c => c.ChapterFile)
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
                c.Wanted,
                HasFile = c.ChapterFileId != null,
                FileRemovedAt = c.ChapterFileId == null ? c.FileRemovedAt : null,
                FilePath = c.ChapterFile != null ? c.ChapterFile.RelativePath : null,
                // Where the file came from: a registered source's name, the literal "import" for a
                // file the user brought in from disk, or "torrent:{indexer}" for a grabbed release.
                // Only the first kind is ever replaced by a source-switch re-download.
                FileSourceName = c.ChapterFile != null ? c.ChapterFile.SourceName : null,
                FileReleaseName = c.ChapterFile != null ? c.ChapterFile.ReleaseName : null,
                // Only the quality columns; a new instance in a projection is never tracked.
                File = c.ChapterFile == null ? null : new ChapterFile
                {
                    Id = c.ChapterFile.Id,
                    RelativePath = c.ChapterFile.RelativePath,
                    Size = c.ChapterFile.Size,
                    SourceName = c.ChapterFile.SourceName,
                    ReleaseName = c.ChapterFile.ReleaseName,
                    Tier = c.ChapterFile.Tier,
                    Group = c.ChapterFile.Group,
                    PageCount = c.ChapterFile.PageCount,
                    MedianWidth = c.ChapterFile.MedianWidth,
                    MedianHeight = c.ChapterFile.MedianHeight,
                    ImageFormat = c.ChapterFile.ImageFormat,
                    MeasuredAtUtc = c.ChapterFile.MeasuredAtUtc,
                    Trusted = c.ChapterFile.Trusted
                }
            })
            .ToListAsync(ct);
        var evaluator = await upgrades.ForSeriesAsync(seriesId, ct);

        // When a chapter's backing file is a volume/compilation CBZ, surface that
        // volume so the UI can show "Vol.x Ch.y" even for scrape-source chapters that
        // carry no volume metadata (parsing can't run inside the EF query, so it's
        // done here in memory over the materialized rows).
        var chapters = ChapterOrder.Sort(rows, c => c.Number, c => c.Volume, c => c.Id).Select(c => new
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
            c.Wanted,
            c.HasFile,
            c.FileRemovedAt,
            c.FilePath,
            c.FileSourceName,
            c.FileReleaseName,
            FileVolume = VolumeFileLabel(c.FilePath),
            FileQuality = c.File is null ? null
                : evaluator?.Quality(c.File, c.Language) ?? ChapterFileQualityDto.From(c.File)
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

    /// <summary>
    /// Sets whether the user wants this chapter. Nothing in the download pipeline writes this flag,
    /// so a choice made here survives Smart top-ups and series monitor-mode changes.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut("{id:int}/wanted")]
    public async Task<IActionResult> SetWanted(int id, [FromQuery] bool wanted, CancellationToken ct)
    {
        var chapter = await db.Chapters.FindAsync([id], ct);
        if (chapter is null)
        {
            return NotFound();
        }

        chapter.Wanted = wanted;
        await db.SaveChangesAsync(ct);
        return Ok(new { chapter.Id, chapter.Wanted });
    }

    /// <summary>Sets the wanted flag on a batch of chapters, for the Chapters table's select mode.</summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut("wanted")]
    public async Task<IActionResult> SetWantedBulk(
        [FromBody] SetChaptersWantedRequest request,
        CancellationToken ct)
    {
        if (request.ChapterIds.Length == 0)
        {
            return this.Fail(localizer, "error.chapter.noChaptersSelected");
        }

        var chapters = await db.Chapters
            .Where(c => request.ChapterIds.Contains(c.Id))
            .ToListAsync(ct);

        foreach (var chapter in chapters)
        {
            chapter.Wanted = request.Wanted;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = chapters.Count });
    }

    /// <summary>
    /// Queues a specific set of chapters, for the Chapters table's select mode. One request rather
    /// than a call per chapter so a 200-row selection is a single batch notification instead of 200.
    /// <para>
    /// Deliberately ignores <see cref="Chapter.Wanted"/>: picking rows by hand is a more explicit
    /// statement of intent than the switch is, and the per-row download button already works this way.
    /// </para>
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("download")]
    public async Task<IActionResult> DownloadBulk(
        [FromBody] DownloadChaptersRequest request,
        CancellationToken ct)
    {
        if (request.ChapterIds.Length == 0)
        {
            return this.Fail(localizer, "error.chapter.noChaptersSelected");
        }

        var chapters = await db.Chapters
            .Where(c => request.ChapterIds.Contains(c.Id) && c.ChapterFileId == null)
            .Select(c => new { c.Id, c.SeriesId, c.Number, SeriesTitle = c.Series!.Title })
            .ToListAsync(ct);

        // A selection can span series, and one unmapped series must not throw away the rest; the
        // bulk enqueue skips that series and reports why.
        var ordered = chapters.OrderBy(c => c.Number ?? decimal.MaxValue).ThenBy(c => c.Id).ToList();
        var result = await queue.EnqueueChaptersAsync(
            ordered.Select(c => c.Id).ToList(), DownloadOrigin.Manual, currentUser.UserId, ct);

        var titles = ordered.GroupBy(c => c.SeriesId).ToDictionary(g => g.Key, g => g.First().SeriesTitle);
        foreach (var batch in result.Queued.GroupBy(item => item.SeriesId))
        {
            await downloadBatches.QueuedAsync(batch.Key, titles[batch.Key], batch.Select(item => item.Id).ToList());
        }

        return Ok(new { queued = result.Queued.Count, error = result.Error is null ? null : localizer.Get(result.Error) });
    }

    /// <summary>
    /// Bulk-links chapters to a specific file in the series folder — for compilation CBZs
    /// or oddly-named files the automatic linker (<see cref="CbzLinkService"/>) couldn't
    /// match. Creates the backing <see cref="ChapterFile"/> record if the file was never
    /// imported (e.g. an "unrecognized" file surfaced by <c>GET /series/{id}/files</c>).
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut("link")]
    public async Task<IActionResult> Link([FromBody] LinkChaptersRequest request, CancellationToken ct)
    {
        if (request.ChapterIds.Length == 0)
        {
            return this.Fail(localizer, "error.chapter.noChaptersSelected");
        }

        var chapters = await db.Chapters.Where(c => request.ChapterIds.Contains(c.Id)).ToListAsync(ct);
        if (chapters.Count != request.ChapterIds.Length)
        {
            return NotFound();
        }

        var seriesId = chapters[0].SeriesId;
        if (chapters.Any(c => c.SeriesId != seriesId))
        {
            return this.Fail(localizer, "error.chapter.differentSeries");
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series?.RootFolder is null)
        {
            return this.Fail(localizer, "error.chapter.noRootFolder");
        }

        // This is the one place a request-supplied path becomes a stored ChapterFile.RelativePath,
        // and every consumer of that column (delete, ComicInfo rewrite, the reader) joins it back
        // onto the root folder. A bare Path.Combine accepts "..\.." and discards the root outright
        // for an absolute argument, so an EditMetadata holder could point a row at maki.db and a
        // DeleteSeries holder could then delete it. Resolve is the containment check; reject rather
        // than sanitize, so nothing escaping ever reaches the database. ResolveNoLinks also refuses a
        // symlink or junction inside the series folder, which would lead out lexically unseen.
        var absPath = LibraryPaths.ResolveNoLinks(series.RootFolder.Path, request.RelativePath);
        if (absPath is null)
        {
            return this.Fail(localizer, "error.chapter.pathOutsideRoot");
        }

        // "./X/a.cbz" and "../Root/X/a.cbz" resolve inside the root but store a "." or ".." top
        // folder, which SeriesFolders would then hand to rescan and relink as this series' folder.
        var relativePath = Path.GetRelativePath(series.RootFolder.Path, absPath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            LibraryPaths.TopFolder(relativePath) is "" or "." or "..")
        {
            return this.Fail(localizer, "error.chapter.pathOutsideRoot");
        }

        // Same exclusion SeriesFolders.ForAsync uses: a folder another series already owns never
        // becomes this one's, manual link or not. Skipped when the top folder is this series' own
        // folder: legacy data can have another series sharing that same FolderName, and linking
        // into your own folder must not be refused over that.
        if (LibraryPaths.TopFolder(relativePath) is { } topFolder &&
            !LibraryPaths.FolderComparer.Equals(topFolder, series.FolderName))
        {
            var otherFolders = (await db.Series
                    .Where(s => s.RootFolderId == series.RootFolderId && s.Id != series.Id)
                    .Select(s => s.FolderName)
                    .ToListAsync(ct))
                .ToHashSet(LibraryPaths.FolderComparer);
            if (otherFolders.Contains(topFolder))
            {
                return this.Fail(localizer, "error.chapter.pathInOtherSeries");
            }
        }

        if (!System.IO.File.Exists(absPath))
        {
            return this.Fail(localizer, "error.chapter.fileNotFound");
        }

        var file = await db.ChapterFiles
            .FirstOrDefaultAsync(f => f.SeriesId == seriesId && f.RelativePath == relativePath, ct);
        if (file is null)
        {
            file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = relativePath,
                Size = new FileInfo(absPath).Length,
                SourceName = "Manual",
                DateAdded = DateTime.UtcNow
            };
            ChapterFileQualityService.StampTierOnly(file, sourceRegistry.Find(file.SourceName)?.Kind, null);
            db.ChapterFiles.Add(file);
            stats.Record(StatsEventType.ChapterDownloaded, series.Id, series.Title);
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
    [Authorize(Policy = Policies.EditMetadata)]
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

    /// <summary>
    /// "Remove chapters": permanently removes chapter rows, not just their file, for cases like a
    /// broken auto-match that pulled in the wrong show: chapter data is otherwise
    /// additive-only, so bad rows would sit in the library forever. Also deletes the
    /// backing CBZ from disk when this batch drops the last chapter referencing it
    /// (a volume CBZ can back several chapters).
    /// </summary>
    [Authorize(Policy = Policies.DeleteSeries)]
    [HttpDelete]
    public async Task<IActionResult> Delete(
        [FromBody] int[] chapterIds, [FromServices] ChapterFileDeletion deletion, CancellationToken ct)
    {
        if (chapterIds.Length == 0)
        {
            return this.Fail(localizer, "error.chapter.noChaptersSelected");
        }

        var seriesIds = await db.Chapters
            .Where(c => chapterIds.Contains(c.Id))
            .Select(c => c.SeriesId)
            .Distinct()
            .ToListAsync(ct);
        if (seriesIds.Count == 0)
        {
            return Ok(new { deleted = 0 });
        }

        // The root folder below comes from this one series, so a mixed batch would delete series B's
        // file using series A's root path. Same check Link makes, for the same reason.
        if (seriesIds.Count > 1)
        {
            return this.Fail(localizer, "error.chapter.differentSeries");
        }

        var seriesId = seriesIds[0];

        // Rows and files are read and removed under the lock a rescan or import holds, so neither
        // can link a chapter to a file this deletes, or save over a row it removed.
        using var seriesLock = await SeriesLocks.SeriesAsync(seriesId, ct);
        var chapters = await db.Chapters
            .Where(c => chapterIds.Contains(c.Id) && c.SeriesId == seriesId)
            .ToListAsync(ct);
        if (chapters.Count == 0)
        {
            return Ok(new { deleted = 0 });
        }

        var deletingIds = chapters.Select(c => c.Id).ToList();

        // A worker holding one of these would package into the folder after the row is gone.
        if (await SeriesLocks.InFlight(db.DownloadQueue)
                .AnyAsync(q => q.ChapterId != null && deletingIds.Contains(q.ChapterId.Value), ct))
        {
            return this.Conflict(localizer, "error.chapter.activeDownloadDelete");
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);

        var fileIds = chapters
            .Where(c => c.ChapterFileId != null)
            .Select(c => c.ChapterFileId!.Value)
            .ToHashSet();
        var fileIdList = fileIds.ToList();
        var stillReferenced = (await db.Chapters
                .Where(c => c.ChapterFileId != null && fileIdList.Contains(c.ChapterFileId.Value) &&
                            !deletingIds.Contains(c.Id))
                .Select(c => c.ChapterFileId!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        var batchFiles = await db.ChapterFiles.Where(f => fileIdList.Contains(f.Id)).ToListAsync(ct);

        // A manual link (see Link above) can point a second row, in this series or another, at the
        // same physical file, and nested root folders can name it twice, so ChapterFileId alone
        // can't tell if the file is still claimed elsewhere. A row removed in this batch doesn't count
        // as a claim, or two rows pointing at one file that are both removed would each see the other
        // as still holding it and the file would never be deleted. A row kept because another chapter
        // still uses it does count.
        var resolved = batchFiles.ToDictionary(f => f.Id, f => series?.RootFolder is null
            ? null
            : LibraryPaths.ResolveForDelete(series.RootFolder.Path, f.RelativePath));
        var claimedPaths = await deletion.ClaimedAsync(
            resolved.Values.OfType<string>().ToList(),
            fileIds.Where(id => !stillReferenced.Contains(id)).ToHashSet(),
            ct);

        // Collected here instead of deleted in place: rows are saved first, and only a successful
        // save unlocks touching the filesystem.
        var toDeleteFromDisk = new List<(string AbsPath, string RelativePath)>();
        foreach (var file in batchFiles)
        {
            if (stillReferenced.Contains(file.Id))
            {
                continue;
            }

            // Never File.Delete a bare Combine: a row written before the check in Link, or by any
            // future path that skips it, would delete whatever it points at outside the library.
            var absPath = resolved[file.Id];
            if (absPath is null || !claimedPaths.Contains(absPath))
            {
                if (series?.RootFolder is not null && absPath is null)
                {
                    logger.LogWarning("Refusing to delete {File}: resolves outside {Root} or through a linked folder",
                        file.RelativePath, series.RootFolder.Path);
                }

                if (absPath is not null)
                {
                    toDeleteFromDisk.Add((absPath, file.RelativePath));
                }
            }

            archives.Invalidate(file.Id);
            db.ChapterFiles.Remove(file);
        }

        db.Chapters.RemoveRange(chapters);
        await db.SaveChangesAsync(ct);

        // Rows are already committed, so a failure here just orphans a file for Health's "unlinked"
        // detection to pick up. Runs without the request's own cancellation token for that reason.
        foreach (var (absPath, relativePath) in toDeleteFromDisk)
        {
            try
            {
                System.IO.File.Delete(absPath);
            }
            catch (DirectoryNotFoundException)
            {
                // Containing directory is already gone, so the file is effectively deleted.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {File}, removing records anyway", relativePath);
            }
        }

        return Ok(new { deleted = chapters.Count });
    }

    /// <summary>
    /// "Delete file": deletes the files behind these chapters and keeps the chapters, with everyone's
    /// reads, marked removed so nothing downloads them again. A volume file backs several chapters,
    /// so every chapter on it loses it, not only the ones named; the dialog says so before sending.
    /// </summary>
    [Authorize(Policy = Policies.DeleteSeries)]
    [HttpPost("deletefiles")]
    public async Task<IActionResult> DeleteFiles(
        [FromBody] int[] chapterIds, [FromServices] ChapterFileDeletion deletion, CancellationToken ct)
    {
        if (chapterIds.Length == 0)
        {
            return this.Fail(localizer, "error.chapter.noChaptersSelected");
        }

        var seriesIds = await db.Chapters
            .Where(c => chapterIds.Contains(c.Id))
            .Select(c => c.SeriesId)
            .Distinct()
            .ToListAsync(ct);
        if (seriesIds.Count == 0)
        {
            return Ok(new ChapterFileDeletion.Result(0, 0, 0, 0));
        }

        if (seriesIds.Count > 1)
        {
            return this.Fail(localizer, "error.chapter.differentSeries");
        }

        var seriesId = seriesIds[0];
        using var seriesLock = await SeriesLocks.SeriesAsync(seriesId, ct);
        var fileIds = await db.Chapters
            .Where(c => chapterIds.Contains(c.Id) && c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => c.ChapterFileId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (fileIds.Count == 0)
        {
            return Ok(new ChapterFileDeletion.Result(0, 0, 0, 0));
        }

        // Every chapter on these files, not just the named ones: a worker packaging any of them would
        // write into a file this is about to delete.
        if (await SeriesLocks.InFlight(db.DownloadQueue)
                .AnyAsync(q => q.ChapterId != null &&
                               db.Chapters.Any(c => c.Id == q.ChapterId && c.ChapterFileId != null &&
                                                    fileIds.Contains(c.ChapterFileId.Value)), ct))
        {
            return this.Conflict(localizer, "error.chapter.activeDownloadDelete");
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstAsync(s => s.Id == seriesId, ct);
        if (series.RootFolder is null)
        {
            return this.Fail(localizer, "error.series.noRootFolder");
        }

        var files = await db.ChapterFiles.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);
        return Ok(await deletion.DeleteAsync(series, files, ct));
    }

    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("{id:int}/search")]
    public async Task<IActionResult> Search(int id, CancellationToken ct)
    {
        try
        {
            var item = await queue.EnqueueChapterAsync(id, ct, DownloadOrigin.Manual, currentUser.UserId);
            return item is null
                ? this.Conflict(localizer, "error.chapter.alreadyQueued")
                : Ok(new { queueItemId = item.Id });
        }
        catch (EnqueueRefusedException ex)
        {
            return this.Fail(localizer, ex.Key);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Queues this chapter pinned to one specific source mapping — the user picked a particular
    /// source's copy, e.g. from the compare view, rather than letting priority order decide.
    /// <see cref="DownloadQueueService.EnqueueChapterAsync"/> carries the pin through to resolution
    /// and, if the chapter is already queued but not yet actively fetching, overrides that row's
    /// pin in place instead of being dropped like a duplicate plain enqueue. A chapter that already has
    /// a file is replaced through the upgrade gate, so the old copy lands in the trash.
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("{id:int}/download-from")]
    public async Task<IActionResult> DownloadFrom(
        int id, [FromBody] DownloadChapterFromRequest request, [FromServices] UpgradeEvaluationService upgrades,
        CancellationToken ct)
    {
        var chapter = await db.Chapters.FindAsync([id], ct);
        if (chapter is null)
        {
            return NotFound();
        }

        var mapping = await db.SourceMappings.FindAsync([request.SourceMappingId], ct);
        if (mapping is null)
        {
            return this.Fail(localizer, "error.chapter.mappingNotFound");
        }

        if (mapping.SeriesId != chapter.SeriesId)
        {
            return this.Fail(localizer, "error.chapter.mappingWrongSeries");
        }

        if (!mapping.Enabled)
        {
            return this.Fail(localizer, "error.chapter.mappingDisabled");
        }

        try
        {
            var replaceInfo = await ReplaceInfoAsync(id, mapping.SourceName,
                await upgrades.ForSeriesAsync(chapter.SeriesId, ct), ct);
            var item = await queue.EnqueueChapterAsync(
                id, ct, DownloadOrigin.Manual, currentUser.UserId, request.SourceMappingId, replaceInfo);
            if (item is null)
            {
                return this.Conflict(localizer, "error.chapter.alreadyQueued");
            }

            // Already fetching or downloading from wherever it resolved to, and not redirected.
            return item.PreferredMappingId == request.SourceMappingId
                ? Ok(new { queueItemId = item.Id })
                : this.Conflict(localizer, "error.chapter.alreadyDownloading");
        }
        catch (EnqueueRefusedException ex)
        {
            return this.Fail(localizer, ex.Key);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Re-downloads this series' chapters that came from some source other than
    /// <c>SourceName</c> — the follow-up to ranking sources in the comparison view, where the
    /// winner is usually not the one the existing files came from.
    /// <para>
    /// Only files that came from a <i>scrape source</i> are candidates. <c>ChapterFile.SourceName</c>
    /// also carries the sentinel "import" for files the user brought in from disk and
    /// "torrent:{indexer}" for grabbed releases; replacing either because someone expressed a
    /// preference between two scrape sources would be destructive in a way nothing here asked for.
    /// Testing the name against the source registry covers both without hardcoding either.
    /// </para>
    /// <para>
    /// Nothing forces the download to use that source: priority already prefers it, and a chapter it
    /// turns out not to serve is better fetched from the next source than not at all. What this does
    /// avoid is queueing chapters the preferred source doesn't list at all, which would re-fetch them
    /// from the very source they already came from.
    /// </para>
    /// <para>
    /// Each chapter goes through the upgrade gate as a forced replacement, so its old copy lands in
    /// the trash and can be reverted from Activity.
    /// </para>
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("redownload")]
    public async Task<IActionResult> Redownload(
        [FromBody] RedownloadRequest request, [FromServices] UpgradeEvaluationService upgrades, CancellationToken ct)
    {
        var mapping = await db.SourceMappings
            .FirstOrDefaultAsync(m => m.SeriesId == request.SeriesId && m.SourceName == request.SourceName, ct);
        if (mapping is null || sourceRegistry.Find(request.SourceName) is not { } source)
        {
            return NotFound();
        }

        var candidates = (await db.Chapters
                .Where(c => c.SeriesId == request.SeriesId &&
                            c.ChapterFile != null &&
                            c.ChapterFile.SourceName != request.SourceName)
                .Select(c => new { c.Id, c.Number, From = c.ChapterFile!.SourceName })
                .ToListAsync(ct))
            // Registry lookup can't run in SQL, and the candidate set is one series' chapters.
            .Where(c => sourceRegistry.Find(c.From) is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            return Ok(new { queued = 0, unavailable = 0 });
        }

        // One listing for the whole batch, not one per chapter: it is per series anyway, and this
        // shares the same single-flighted cache the download path resolves against.
        var listed = (await chapterLists.GetAsync(source, mapping.SourceSeriesId, mapping.LanguageFilter, ct))
            .Where(c => c.Number is not null)
            .Select(c => c.Number!.Value)
            .ToHashSet();

        var evaluator = await upgrades.ForSeriesAsync(request.SeriesId, ct);
        var queued = 0;
        var unavailable = 0;
        foreach (var chapter in candidates)
        {
            if (chapter.Number is null || !listed.Contains(chapter.Number.Value))
            {
                unavailable++;
                continue;
            }

            try
            {
                var replaceInfo = await ReplaceInfoAsync(chapter.Id, request.SourceName, evaluator, ct);
                if (await queue.EnqueueChapterAsync(chapter.Id, ct, DownloadOrigin.Manual, currentUser.UserId,
                        replaceInfo: replaceInfo) is not null)
                {
                    queued++;
                }
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Could not queue chapter {ChapterId} for re-download", chapter.Id);
            }
        }

        return Ok(new { queued, unavailable });
    }

    /// <summary>
    /// What a re-download needs to replace the chapter's file through the upgrade gate instead of
    /// overwriting it. Null for a chapter without a file, for one whose file also backs other
    /// chapters (a volume), and for a file the packaged zip cannot stand in for (a PDF): those still
    /// download to their own path.
    /// </summary>
    private async Task<UpgradeInfo?> ReplaceInfoAsync(
        int chapterId, string sourceName, UpgradeEvaluator? evaluator, CancellationToken ct)
    {
        var row = await db.Chapters.AsNoTracking()
            .Where(c => c.Id == chapterId && c.ChapterFileId != null)
            .Select(c => new
            {
                c.Language,
                File = c.ChapterFile!,
                Shared = db.Chapters.Count(o => o.ChapterFileId == c.ChapterFileId) > 1
            })
            .FirstOrDefaultAsync(ct);
        if (row is null || row.Shared || !UpgradeTrash.IsReplaceable(row.File.RelativePath))
        {
            return null;
        }

        var tier = QualityTierResolver.Resolve(sourceRegistry.Find(sourceName)?.Kind, null,
            Path.GetFileName(row.File.RelativePath), isVolume: false);
        return new UpgradeInfo
        {
            ChapterFileId = row.File.Id,
            Force = true,
            IgnoreGuards = currentUser.Permissions.Grants(MakiPermission.ManageDownloadQueue),
            ProfileId = evaluator?.Profile.Id ?? 0,
            ProfileVersion = evaluator?.Profile.Version ?? 0,
            Before = UpgradeEvaluator.Snapshot(row.File, evaluator?.Evaluate(row.File, row.Language)?.Score.Score ?? 0),
            Predicted = new QualitySnapshot { Tier = QualitySnapshot.TierName(tier), SourceName = sourceName }
        };
    }
}
