using Mangarr.Core.ComicInfo;
using Mangarr.Core.Entities;
using Mangarr.Core.Parsing;
using Mangarr.Core.Sources;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

public record RescanResult(int NewFiles, int Relinked, int Removed, int Unrecognized);

/// <summary>
/// Shared logic for adopting CBZ files that Mangarr didn't download page-by-page
/// (library imports, completed torrents): create ChapterFile records, link
/// chapters by parsed number (or by volume range when volume data exists), and
/// standardize the ComicInfo.xml inside each adopted file so Kavita groups it
/// with Mangarr's own downloads.
/// </summary>
public class CbzLinkService(
    MangarrDbContext db, SourceRegistry sources, KavitaScanService kavitaScans, ILogger<CbzLinkService> logger)
{
    /// <param name="files">Absolute paths of CBZ files, already inside the series folder.</param>
    /// <param name="seriesDir">Absolute path of the series folder (for relative paths).</param>
    /// <param name="progress">Invoked before each file with (1-based index, total file count).</param>
    /// <param name="updateComicInfo">When false, adopted files keep their ComicInfo.xml untouched.</param>
    public async Task<(int Linked, int Unrecognized)> LinkFilesAsync(
        Series series, string seriesDir, IEnumerable<string> files, string sourceName,
        Func<int, int, Task>? progress = null, bool updateComicInfo = true, CancellationToken ct = default)
    {
        var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
        var linked = 0;
        var unrecognized = 0;

        var ordered = files.OrderBy(f => f).ToList();
        var index = 0;
        var unlinkedVolumeFiles = new List<(ParsedReleaseFile Parsed, ChapterFile Record)>();
        var volumeFiles = new List<(int FileId, string AbsolutePath, ParsedReleaseFile Parsed)>();
        foreach (var file in ordered)
        {
            if (progress != null)
            {
                await progress(++index, ordered.Count);
            }

            var parsed = ReleaseNameParser.ParseFileName(file);
            var relativePath = Path.GetRelativePath(seriesDir, file);

            var chapterFile = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = Path.Combine(series.FolderName, relativePath),
                Size = new FileInfo(file).Length,
                SourceName = sourceName,
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(chapterFile);
            await db.SaveChangesAsync(ct); // need the file id for linking

            List<Chapter> matched = [];
            if (!parsed.IsRecognized)
            {
                unrecognized++;
            }
            else
            {
                matched = LinkChapters(chapters, parsed, chapterFile.Id);
                if (matched.Count == 0 && parsed.IsVolume)
                {
                    // No volume metadata to range-match against — read the chapters the
                    // compilation actually contains from its page file names.
                    matched = LinkVolumeByContents(chapters, file, chapterFile.Id);
                }

                if (parsed.IsVolume)
                {
                    volumeFiles.Add((chapterFile.Id, file, parsed));
                }

                if (matched.Count > 0)
                {
                    linked++;
                }
                else if (parsed.IsVolume)
                {
                    unlinkedVolumeFiles.Add((parsed, chapterFile));
                }
            }

            if (updateComicInfo)
            {
                StandardizeComicInfo(file, series, parsed, matched.Count == 1 ? matched[0] : null, chapterFile);
            }
        }

        // Volume CBZs that matched nothing usually mean the chapter rows carry no
        // volume info (scrape sources don't have it). Pull the chapter→volume map
        // from a volume-capable source and retry those files with exact ranges.
        if (unlinkedVolumeFiles.Count > 0 && await TryBackfillChapterVolumesAsync(series, chapters, ct))
        {
            linked += unlinkedVolumeFiles.Count(x => LinkChapters(chapters, x.Parsed, x.Record.Id).Count > 0);
        }

        // A volume file that range-matched some chapters can still contain others the
        // provider assigned to a different volume (compilation vs provider boundaries
        // disagree). Link any still-missing chapter its page markers prove it contains.
        linked += FillVolumeContents(chapters, volumeFiles);

        await EstimateCompletedVolumeLinksAsync(series, chapters, ct);
        await db.SaveChangesAsync(ct);
        if (ordered.Count > 0)
        {
            kavitaScans.QueueScan(seriesDir, series.Id);
        }

        return (linked, unrecognized);
    }

    /// <summary>
    /// Reconciles a series' ChapterFile records with the folder on disk:
    /// removes records for deleted files, adopts files that appeared outside
    /// Mangarr, and retries linking files that matched no chapter earlier
    /// (e.g. volume CBZs adopted before chapters had volume metadata).
    /// </summary>
    public async Task<RescanResult> RescanSeriesAsync(Series series, CancellationToken ct = default)
    {
        var rootFolder = series.RootFolder
            ?? throw new InvalidOperationException("Series has no root folder loaded");
        var seriesDir = Path.Combine(rootFolder.Path, series.FolderName);

        var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
        var dbFiles = await db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct);

        var onDisk = Directory.Exists(seriesDir)
            ? Directory.GetFiles(seriesDir, "*.cbz", SearchOption.AllDirectories)
            : [];
        var diskRelPaths = onDisk
            .Select(f => Path.Combine(series.FolderName, Path.GetRelativePath(seriesDir, f)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Files deleted from disk: drop the record, free the chapters.
        var removed = 0;
        foreach (var dbFile in dbFiles.Where(f => !diskRelPaths.Contains(f.RelativePath)).ToList())
        {
            foreach (var chapter in chapters.Where(c => c.ChapterFileId == dbFile.Id))
            {
                chapter.ChapterFileId = null;
            }

            db.ChapterFiles.Remove(dbFile);
            dbFiles.Remove(dbFile);
            removed++;
        }

        // 2. Known files no chapter points at: retry matching with current chapter data.
        var linkedFileIds = chapters
            .Where(c => c.ChapterFileId != null)
            .Select(c => c.ChapterFileId!.Value)
            .ToHashSet();
        var unlinkedFiles = dbFiles
            .Where(f => !linkedFileIds.Contains(f.Id))
            .Select(f => (File: f, Parsed: ReleaseNameParser.ParseFileName(f.RelativePath)))
            .ToList();
        if (unlinkedFiles.Any(x => x.Parsed.IsVolume))
        {
            await TryBackfillChapterVolumesAsync(series, chapters, ct);
        }

        var relinked = 0;
        foreach (var (dbFile, parsed) in unlinkedFiles)
        {
            if (!parsed.IsRecognized)
            {
                continue;
            }

            var matched = LinkChapters(chapters, parsed, dbFile.Id);
            if (matched.Count == 0 && parsed.IsVolume)
            {
                var absolutePath = Path.Combine(rootFolder.Path, dbFile.RelativePath);
                matched = LinkVolumeByContents(chapters, absolutePath, dbFile.Id);
            }

            if (matched.Count > 0)
            {
                relinked++;
            }
        }

        // Re-link chapters a volume file on disk demonstrably contains (per its embedded
        // page markers) but that stayed missing — chapters that appeared after the file
        // was first linked, or that fell outside the provider's volume range for this file.
        // Runs on already-linked volume files too, which the unlinked-file retry above skips.
        var volumeFilesOnDisk = dbFiles
            .Select(f => (f.Id, Path.Combine(rootFolder.Path, f.RelativePath), ReleaseNameParser.ParseFileName(f.RelativePath)))
            .ToList();
        relinked += FillVolumeContents(chapters, volumeFilesOnDisk);

        await db.SaveChangesAsync(ct);

        // 3. Files on disk we have no record of yet.
        var knownRelPaths = dbFiles.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFiles = onDisk
            .Where(f => !knownRelPaths.Contains(Path.Combine(series.FolderName, Path.GetRelativePath(seriesDir, f))))
            .ToList();
        var (linkedNew, unrecognized) = await LinkFilesAsync(series, seriesDir, newFiles, "rescan", ct: ct);
        if (newFiles.Count == 0)
        {
            // LinkFilesAsync runs the estimator itself; cover the no-new-files path too.
            await EstimateCompletedVolumeLinksAsync(series, chapters, ct);
            await db.SaveChangesAsync(ct);
        }

        return new RescanResult(linkedNew, relinked, removed, unrecognized);
    }

    /// <summary>
    /// Re-standardizes the ComicInfo.xml inside every CBZ the series owns —
    /// files Mangarr downloaded itself are already standard and come back as
    /// no-ops. Used by the bulk "update ComicInfo" action.
    /// </summary>
    public async Task<(int Updated, int Total)> UpdateComicInfoAsync(Series series, CancellationToken ct = default)
    {
        var rootFolder = series.RootFolder
            ?? throw new InvalidOperationException("Series has no root folder loaded");

        var files = await db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct);
        var chapters = await db.Chapters
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var chapterFile in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(rootFolder.Path, chapterFile.RelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            var parsed = ReleaseNameParser.ParseFileName(chapterFile.RelativePath);
            var linked = chapters.Where(c => c.ChapterFileId == chapterFile.Id).ToList();
            if (StandardizeComicInfo(path, series, parsed, linked.Count == 1 ? linked[0] : null, chapterFile))
            {
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        if (updated > 0)
        {
            kavitaScans.QueueScan(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        }

        return (updated, files.Count);
    }

    /// <summary>
    /// Fills Chapter.Volume from a volume-capable source (MangaDex, whose feed still
    /// lists delisted chapters of licensed titles with their volume assignment) for
    /// chapter rows that have none — scrape sources don't carry volume info. Returns
    /// true when at least one chapter gained a volume. Existing volumes are kept.
    /// </summary>
    private async Task<bool> TryBackfillChapterVolumesAsync(Series series, List<Chapter> chapters, CancellationToken ct)
    {
        if (!chapters.Any(c => c.Volume == null && c.Number != null))
        {
            return false;
        }

        var mappings = await db.SourceMappings
            .Where(m => m.SeriesId == series.Id && m.Enabled)
            .OrderBy(m => m.Priority)
            .ToListAsync(ct);
        foreach (var mapping in mappings)
        {
            if (sources.Find(mapping.SourceName) is not IChapterVolumeSource volumeSource)
            {
                continue;
            }

            try
            {
                var volumeByNumber = await volumeSource.GetChapterVolumesAsync(mapping.SourceSeriesId, ct);
                var filled = 0;
                foreach (var chapter in chapters.Where(c => c.Volume == null && c.Number != null))
                {
                    if (volumeByNumber.TryGetValue(chapter.Number!.Value, out var volume))
                    {
                        chapter.Volume = volume;
                        filled++;
                    }
                }

                if (filled > 0)
                {
                    logger.LogInformation(
                        "Backfilled volume info for {Count} chapters of '{Title}' from {Source}",
                        filled, series.Title, mapping.SourceName);
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Volume backfill from {Source} failed for '{Title}'",
                    mapping.SourceName, series.Title);
            }
        }

        return false;
    }

    /// <summary>
    /// Fallback for series where no source maps chapters to volumes: if the series
    /// is finished (completed or cancelled) and the volume CBZs on disk cover every
    /// volume the metadata provider knows about, every chapter is provably present.
    /// Unlinked chapters are assigned to files by proportional position (chapters ÷
    /// volumes) — approximate per file, but the completeness itself is certain.
    /// Ongoing series are excluded because their chapter count runs ahead of the volumes.
    /// </summary>
    private async Task EstimateCompletedVolumeLinksAsync(Series series, List<Chapter> chapters, CancellationToken ct)
    {
        if (series.Status is not (SeriesStatus.Completed or SeriesStatus.Cancelled) ||
            series.TotalVolumes is not > 0 || series.TotalChapters is not > 0 ||
            !chapters.Any(c => c.ChapterFileId == null && c.Number != null))
        {
            return;
        }

        var volumeFiles = (await db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct))
            .Select(f => (File: f, Parsed: ReleaseNameParser.ParseFileName(f.RelativePath)))
            .Where(x => x.Parsed.IsVolume)
            .Select(x => (x.File, Start: x.Parsed.Volume!.Value, End: x.Parsed.VolumeEnd ?? x.Parsed.Volume!.Value))
            .ToList();

        var covered = volumeFiles.SelectMany(x => Enumerable.Range(x.Start, x.End - x.Start + 1)).ToHashSet();
        if (Enumerable.Range(1, series.TotalVolumes.Value).Any(v => !covered.Contains(v)))
        {
            return;
        }

        var chaptersPerVolume = (decimal)series.TotalChapters.Value / series.TotalVolumes.Value;
        foreach (var chapter in chapters.Where(c => c.ChapterFileId == null && c.Number is > 0))
        {
            var volume = chapter.Volume
                ?? Math.Clamp((int)Math.Ceiling(chapter.Number!.Value / chaptersPerVolume), 1, series.TotalVolumes.Value);
            chapter.ChapterFileId = volumeFiles
                .FirstOrDefault(x => volume >= x.Start && volume <= x.End)
                .File?.Id ?? chapter.ChapterFileId;
        }
    }

    /// <summary>
    /// Rewrites the file's embedded ComicInfo.xml to Mangarr's standard so Kavita
    /// groups it with downloaded chapters. Failures are logged, never fatal — the
    /// file stays linked either way.
    /// </summary>
    private bool StandardizeComicInfo(
        string file, Series series, ParsedReleaseFile parsed, Chapter? chapter, ChapterFile chapterFile)
    {
        try
        {
            if (ComicInfoUpdater.UpdateFile(file, series, parsed, chapter))
            {
                chapterFile.Size = new FileInfo(file).Length; // rewrite changed the archive size
                logger.LogInformation("Standardized ComicInfo.xml in {File}", chapterFile.RelativePath);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update ComicInfo.xml in {File}", chapterFile.RelativePath);
        }

        return false;
    }

    /// <summary>
    /// Links a volume/compilation CBZ to the chapters it actually contains by reading the
    /// chapter markers embedded in its page file names — the ground truth when the chapter
    /// rows carry no volume info to range-match against (scrape sources) or the compilation's
    /// boundaries disagree with the metadata provider's. Returns the chapters that were linked.
    /// </summary>
    private List<Chapter> LinkVolumeByContents(List<Chapter> chapters, string cbzPath, int chapterFileId)
    {
        var numbers = VolumeChapterScanner.ScanCbz(cbzPath);
        if (numbers.Count == 0)
        {
            return [];
        }

        List<Chapter> targets = [];
        foreach (var number in numbers)
        {
            var match = chapters.FirstOrDefault(c => c.Number == number && c.ChapterFileId == null)
                        ?? chapters.FirstOrDefault(c => c.Number == number);
            if (match != null && !targets.Contains(match))
            {
                targets.Add(match);
            }
        }

        foreach (var chapter in targets)
        {
            chapter.ChapterFileId = chapterFileId;
        }

        if (targets.Count > 0)
        {
            logger.LogInformation(
                "Linked {Count} chapters to volume file {File} from its embedded page names",
                targets.Count, Path.GetFileName(cbzPath));
        }

        return targets;
    }

    /// <summary>
    /// For each volume/compilation CBZ, links any chapter the file demonstrably contains
    /// (per the chapter markers in its page file names) that is still unlinked. Unlike the
    /// initial adopt path this runs even when the file already links other chapters, so a
    /// file whose provider volume-range covered only part of its contents still gets the
    /// rest — and a rescan picks up chapters that appeared after the file was first linked.
    /// Only touches currently-unlinked chapters, so it never steals one from another file.
    /// Returns the number of chapters newly linked.
    /// </summary>
    private int FillVolumeContents(
        List<Chapter> chapters, IEnumerable<(int FileId, string AbsolutePath, ParsedReleaseFile Parsed)> files)
    {
        var linked = 0;
        foreach (var (fileId, path, parsed) in files)
        {
            if (!parsed.IsVolume || !File.Exists(path))
            {
                continue;
            }

            var filled = 0;
            foreach (var number in VolumeChapterScanner.ScanCbz(path))
            {
                var chapter = chapters.FirstOrDefault(c => c.Number == number && c.ChapterFileId == null);
                if (chapter != null)
                {
                    chapter.ChapterFileId = fileId;
                    filled++;
                }
            }

            if (filled > 0)
            {
                linked += filled;
                logger.LogInformation(
                    "Linked {Count} previously-missing chapter(s) to volume file {File} from its embedded page names",
                    filled, Path.GetFileName(path));
            }
        }

        return linked;
    }

    /// <summary>Points matching chapters at the file; returns the chapters that were linked.</summary>
    private static List<Chapter> LinkChapters(List<Chapter> chapters, ParsedReleaseFile parsed, int chapterFileId)
    {
        List<Chapter> targets = [];
        if (parsed.IsChapter)
        {
            var match = chapters.FirstOrDefault(c => c.Number == parsed.Number && c.ChapterFileId == null)
                        ?? chapters.FirstOrDefault(c => c.Number == parsed.Number);
            if (match != null)
            {
                targets.Add(match);
            }
        }
        else if (parsed.IsVolume)
        {
            var end = parsed.VolumeEnd ?? parsed.Volume;
            targets = chapters
                .Where(c => c.Volume >= parsed.Volume && c.Volume <= end && c.ChapterFileId == null)
                .ToList();
        }

        foreach (var chapter in targets)
        {
            chapter.ChapterFileId = chapterFileId;
        }

        return targets;
    }
}
