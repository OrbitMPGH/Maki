using Maki.Core.ComicInfo;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record RescanResult(int NewFiles, int Relinked, int Removed, int Unrecognized, bool RootUnavailable = false);

/// <summary>
/// Shared logic for adopting CBZ files that Maki didn't download page-by-page
/// (library imports, completed torrents): create ChapterFile records, link
/// chapters by parsed number (or by volume range when volume data exists), and
/// standardize the ComicInfo.xml inside each adopted file so Kavita groups it
/// with Maki's own downloads.
/// </summary>
public class CbzLinkService(
    MakiDbContext db, SourceRegistry sources, KavitaScanService kavitaScans,
    StatsEventService stats, ReaderArchiveCache archives, SourceAvailability sourceAvailability,
    ChapterFileQualityService quality, IAppSettings settings, ILogger<CbzLinkService> logger)
{
    /// <param name="files">Absolute paths of CBZ files, already inside the series folder.</param>
    /// <param name="seriesDir">Absolute path of the series folder (for relative paths).</param>
    /// <param name="progress">Invoked before each file with (1-based index, total file count).</param>
    /// <param name="updateComicInfo">When false, adopted files keep their ComicInfo.xml untouched.</param>
    /// <param name="replaceExisting">
    /// Whether a chapter that already has a file may be re-pointed at one of these. False links
    /// only chapters nothing backs yet, so an adopted archive can never quietly orphan the file a
    /// chapter is being read from today.
    /// </param>
    /// <param name="displaceableFileIds">
    /// The only existing files these may take chapters off, volume files included. Without it a
    /// volume may displace single-chapter files but never another volume; a torrent upgrade passes the
    /// files its verdict said it replaces.
    /// </param>
    /// <param name="language">
    /// When set, only chapters of this language are linked, by chapter and by volume alike; a torrent
    /// upgrade passes the language its verdict judged.
    /// </param>
    public async Task<(int Linked, int Unrecognized)> LinkFilesAsync(
        Series series, string seriesDir, IEnumerable<string> files, string sourceName,
        Func<int, int, Task>? progress = null, bool updateComicInfo = true, string? releaseName = null,
        bool replaceExisting = true, CancellationToken ct = default, IReadOnlySet<int>? displaceableFileIds = null,
        string? language = null)
    {
        var chapters = (await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct))
            .Where(c => language is null || string.Equals(ChapterFileLanguage.Of(c), language, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var volumeFileIds = await VolumeFileIdsAsync(series.Id, ct);
        var linked = 0;
        var unrecognized = 0;
        var created = 0;

        // Every row the series already has, keyed the way two paths count as one file. A torrent
        // whose import is re-run (a poll cut off before the queue row was saved, a parked item the
        // user settles after the job already placed its files) hands over paths that are already
        // in the folder and already have a row; inserting again gave one file two rows.
        var existing = (await db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct))
            .GroupBy(f => LibraryPaths.ComparisonKey(f.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Id).First(), StringComparer.OrdinalIgnoreCase);

        // The folder the files are actually in. Not always Series.FolderName: a keep-new-standard
        // import links the original folder while FolderName already names the standard one.
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(seriesDir));
        var ordered = files.OrderBy(f => f).ToList();
        var index = 0;
        var unlinkedVolumeFiles = new List<(ParsedReleaseFile Parsed, ChapterFile Record, string Path)>();
        var volumeFiles = new List<(int FileId, string AbsolutePath, ParsedReleaseFile Parsed)>();
        foreach (var file in ordered)
        {
            if (progress != null)
            {
                await progress(++index, ordered.Count);
            }

            var parsed = ReleaseNameParser.ParseFileName(file);
            var relativePath = Path.Combine(folderName, Path.GetRelativePath(seriesDir, file));

            var key = LibraryPaths.ComparisonKey(relativePath);
            if (existing.TryGetValue(key, out var chapterFile))
            {
                // The spelling on disk wins: a row written under the other separator, or with
                // different casing, is repaired here rather than duplicated.
                chapterFile.RelativePath = relativePath;
                var size = new FileInfo(file).Length;
                if (chapterFile.Size != size)
                {
                    chapterFile.MeasuredAtUtc = null;
                }

                chapterFile.Size = size;
                chapterFile.ReleaseName ??= releaseName;
            }
            else
            {
                chapterFile = new ChapterFile
                {
                    SeriesId = series.Id,
                    RelativePath = relativePath,
                    Size = new FileInfo(file).Length,
                    SourceName = sourceName,
                    ReleaseName = releaseName,
                    DateAdded = DateTime.UtcNow
                };
                var (kind, group) = quality.ResolveProvenance(chapterFile, null);
                ChapterFileQualityService.StampTierOnly(chapterFile, kind, group);
                db.ChapterFiles.Add(chapterFile);
                await db.SaveChangesAsync(ct); // need the file id for linking
                existing[key] = chapterFile;
                created++;
            }

            if (parsed.IsVolume)
            {
                volumeFileIds.Add(chapterFile.Id);
            }

            List<Chapter> matched = [];
            if (!parsed.IsRecognized)
            {
                unrecognized++;
            }
            else
            {
                matched = LinkChapters(chapters, parsed, chapterFile.Id, file, volumeFileIds, replaceExisting,
                    displaceableFileIds);
                if (matched.Count == 0 && parsed.IsVolume)
                {
                    // No volume metadata to range-match against — read the chapters the
                    // compilation actually contains from its page file names.
                    matched = LinkVolumeByContents(chapters, parsed, file, chapterFile.Id, volumeFileIds, replaceExisting,
                        displaceableFileIds);
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
                    unlinkedVolumeFiles.Add((parsed, chapterFile, file));
                }
            }

            // The rewrite swaps a new archive over the name, which would turn a hardlinked file (a
            // seeding torrent, an import's zip) into a second full copy.
            if (updateComicInfo && !HardLinks.IsShared(file))
            {
                StandardizeComicInfo(file, series, parsed, matched.Count == 1 ? matched[0] : null, chapterFile);
            }
        }

        // Volume CBZs that matched nothing usually mean the chapter rows carry no
        // volume info (scrape sources don't have it). Pull the chapter→volume map
        // from a volume-capable source and retry those files with exact ranges.
        if (unlinkedVolumeFiles.Count > 0 && await TryBackfillChapterVolumesAsync(series, chapters, ct))
        {
            linked += unlinkedVolumeFiles.Count(
                x => LinkChapters(chapters, x.Parsed, x.Record.Id, x.Path, volumeFileIds, replaceExisting,
                    displaceableFileIds).Count > 0);
        }

        // A volume file that range-matched some chapters can still contain others the
        // provider assigned to a different volume (compilation vs provider boundaries
        // disagree). Link any still-missing chapter its page markers prove it contains.
        linked += FillVolumeContents(chapters, volumeFiles, volumeFileIds, replaceExisting, displaceableFileIds);

        linked += await LinkLoneFileAsync(series, chapters, ct);
        await EstimateCompletedVolumeLinksAsync(series, chapters, ct);
        if (created > 0)
        {
            // One event per adoption batch; value = ChapterFile rows created, so a file that
            // already had a row is never counted as downloaded again.
            stats.Record(StatsEventType.ChapterDownloaded, series.Id, series.Title, created);
        }

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
    /// Maki, and retries linking files that matched no chapter earlier
    /// (e.g. volume CBZs adopted before chapters had volume metadata).
    /// </summary>
    public async Task<RescanResult> RescanSeriesAsync(Series series, CancellationToken ct = default)
    {
        var rootFolder = series.RootFolder
            ?? throw new InvalidOperationException("Series has no root folder loaded");
        using var seriesLock = await SeriesLocks.SeriesAsync(series.Id, ct);

        // An unmounted share looks exactly like every file having been deleted, so a missing root
        // changes nothing, and step 1 below keeps the rows of any folder it could not list.
        var folders = await SeriesFolders.ForAsync(db, series, ct);
        if (!Directory.Exists(rootFolder.Path))
        {
            logger.LogWarning("Skipping rescan of '{Title}': root folder {Root} is not reachable",
                series.Title, rootFolder.Path);
            return new RescanResult(0, 0, 0, 0, RootUnavailable: true);
        }

        var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
        var dbFiles = await db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct);
        var volumeFileIds = dbFiles
            .Where(f => ReleaseNameParser.ParseFileName(f.RelativePath).IsVolume)
            .Select(f => f.Id)
            .ToHashSet();

        // An empty root is usually an unmounted share's mount point, so a folder missing from it
        // says nothing. Under a root that lists anything, a missing folder was deleted or renamed.
        var rootListable = Directory.EnumerateFileSystemEntries(rootFolder.Path).Any();
        var onDisk = new List<(string SeriesDir, string AbsolutePath, string RelativePath)>();
        var readableFolders = new HashSet<string>(LibraryPaths.FolderComparer);
        foreach (var folder in folders)
        {
            // A symlink or junction anywhere below the root would have adoption read, and the
            // ComicInfo rewrite modify, archives outside the library.
            if (LibraryPaths.ResolveNoLinks(rootFolder.Path, folder) is not { } seriesDir)
            {
                continue;
            }

            if (!Directory.Exists(seriesDir))
            {
                if (rootListable)
                {
                    readableFolders.Add(folder);
                }

                continue;
            }

            onDisk.AddRange(LibraryPaths.EnumerateFilesNoLinks(seriesDir)
                .Where(ComicFile.IsComic)
                .Select(f => (seriesDir, f, Path.Combine(folder, Path.GetRelativePath(seriesDir, f)))));
            readableFolders.Add(folder);
        }

        var diskRelPaths = onDisk
            .Select(f => LibraryPaths.ComparisonKey(f.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Files deleted from disk: drop the record, free the chapters. Only rows whose folder
        // was actually listed: a folder that is not there says nothing about the files in it.
        var removed = 0;
        foreach (var dbFile in dbFiles
                     .Where(f => LibraryPaths.TopFolder(f.RelativePath) is { } top
                         ? readableFolders.Contains(top) && !diskRelPaths.Contains(LibraryPaths.ComparisonKey(f.RelativePath))
                         : !File.Exists(LibraryPaths.ResolveNoLinks(rootFolder.Path, LibraryPaths.ComparisonKey(f.RelativePath))))
                     .ToList())
        {
            foreach (var chapter in chapters.Where(c => c.ChapterFileId == dbFile.Id))
            {
                chapter.ChapterFileId = null;
            }

            // Drop the reader's cached page list with the record. SQLite reuses rowids after a
            // delete, so a later adopt can land on this same id with a completely different
            // archive behind it — and the cache's size guard would not notice.
            archives.Invalidate(dbFile.Id);

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

            var absolutePath = LibraryPaths.ResolveNoLinks(rootFolder.Path, LibraryPaths.ComparisonKey(dbFile.RelativePath));
            var matched = LinkChapters(chapters, parsed, dbFile.Id, absolutePath, volumeFileIds);
            if (matched.Count == 0 && parsed.IsVolume)
            {
                if (absolutePath is not null)
                {
                    matched = LinkVolumeByContents(chapters, parsed, absolutePath, dbFile.Id, volumeFileIds);
                }
            }

            if (matched.Count > 0)
            {
                relinked++;
            }
        }

        // Re-link chapters a volume file on disk demonstrably contains (per its embedded
        // page markers) but that stayed missing or sit on a single-chapter file: chapters that
        // appeared after the file was first linked, or that fell outside the provider's volume
        // range for this file. Runs on already-linked volume files too, which the unlinked-file
        // retry above skips.
        var volumeFilesOnDisk = dbFiles
            .Select(f => (
                f.Id,
                AbsolutePath: LibraryPaths.ResolveNoLinks(rootFolder.Path, LibraryPaths.ComparisonKey(f.RelativePath)),
                Parsed: ReleaseNameParser.ParseFileName(f.RelativePath)))
            .Where(f => f.AbsolutePath is not null)
            .Select(f => (f.Id, f.AbsolutePath!, f.Parsed))
            .ToList();
        relinked += FillVolumeContents(chapters, volumeFilesOnDisk, volumeFileIds, replaceExisting: true);

        await db.SaveChangesAsync(ct);

        // 3. Files on disk we have no record of yet.
        var knownRelPaths = dbFiles
            .Select(f => LibraryPaths.ComparisonKey(f.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFiles = onDisk.Where(f => !knownRelPaths.Contains(LibraryPaths.ComparisonKey(f.RelativePath))).ToList();
        var linkedNew = 0;
        var unrecognized = 0;
        var writeComicInfo = newFiles.Count > 0 &&
                             await settings.GetAsync(SettingKeys.LibraryWriteComicInfo, ct) != "false";
        foreach (var group in newFiles.GroupBy(f => f.SeriesDir))
        {
            var (linked, skipped) = await LinkFilesAsync(
                series, group.Key, group.Select(f => f.AbsolutePath), "rescan",
                updateComicInfo: writeComicInfo, ct: ct);
            linkedNew += linked;
            unrecognized += skipped;
        }

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
    /// files Maki downloaded itself are already standard and come back as
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
            // Resolve, not Combine: this opens and rewrites the archive in place, so a stored path
            // escaping the root, lexically or through a link, would turn an EditMetadata grant into
            // arbitrary file modification.
            var path = LibraryPaths.ResolveNoLinks(rootFolder.Path, chapterFile.RelativePath);
            if (path is null || !File.Exists(path))
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

        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var mappings = await db.SourceMappings
            .Where(m => m.SeriesId == series.Id && m.Enabled && !disabledSources.Contains(m.SourceName))
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
    /// A series with one chapter and one file is an unambiguous pairing however the file is named,
    /// and a single-volume work or a one-shot usually has no number in its name to match on at all.
    /// Deliberately limited to a file whose name parses to nothing: a name that does carry a number
    /// disagrees with the chapter rather than saying nothing about it, and a series whose chapter
    /// list has not finished syncing would otherwise adopt the wrong file. Returns 1 when it links.
    /// </summary>
    private async Task<int> LinkLoneFileAsync(Series series, List<Chapter> chapters, CancellationToken ct)
    {
        if (chapters is not [{ ChapterFileId: null } chapter])
        {
            return 0;
        }

        var files = await db.ChapterFiles.Where(f => f.SeriesId == series.Id).Take(2).ToListAsync(ct);
        if (files is not [{ } file] || ReleaseNameParser.ParseFileName(file.RelativePath).IsRecognized)
        {
            return 0;
        }

        chapter.ChapterFileId = file.Id;
        logger.LogInformation(
            "Linked the only chapter of '{Title}' to its only file {File}, which has no number in its name",
            series.Title, file.RelativePath);
        return 1;
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
    /// Rewrites the file's embedded ComicInfo.xml to Maki's standard so Kavita
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
                // Belt and braces: the new size already invalidates the cache entry, but a
                // rewrite that happened to preserve the byte count would not.
                archives.Invalidate(chapterFile.Id);
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
    /// Ids of the series' volume/compilation files. Volumes outrank single-chapter files: a volume
    /// may take a chapter off a single file, a single file never takes one off a volume. The set
    /// is what lets the linkers tell the two apart from a bare <c>ChapterFileId</c>.
    /// </summary>
    private async Task<HashSet<int>> VolumeFileIdsAsync(int seriesId, CancellationToken ct) =>
        (await db.ChapterFiles.Where(f => f.SeriesId == seriesId).Select(f => new { f.Id, f.RelativePath }).ToListAsync(ct))
        .Where(f => ReleaseNameParser.ParseFileName(f.RelativePath).IsVolume)
        .Select(f => f.Id)
        .ToHashSet();

    /// <summary>
    /// Whether a volume file may take this chapter: it is free or sits on a single-chapter file. With a
    /// displaceable set, only the files in it may lose a chapter, volume or not.
    /// </summary>
    private static bool VolumeMayTake(
        Chapter chapter, HashSet<int> volumeFileIds, bool replaceExisting, IReadOnlySet<int>? displaceable = null) =>
        chapter.ChapterFileId is not { } fileId ||
        (replaceExisting && (displaceable?.Contains(fileId) ?? !volumeFileIds.Contains(fileId)));

    /// <summary>
    /// Links a volume/compilation CBZ to the chapters it actually contains by reading the
    /// chapter markers embedded in its page file names — the ground truth when the chapter
    /// rows carry no volume info to range-match against (scrape sources) or the compilation's
    /// boundaries disagree with the metadata provider's. Returns the chapters that were linked.
    /// </summary>
    private List<Chapter> LinkVolumeByContents(
        List<Chapter> chapters, ParsedReleaseFile parsed, string cbzPath, int chapterFileId,
        HashSet<int> volumeFileIds, bool replaceExisting = true, IReadOnlySet<int>? displaceable = null)
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
                        ?? chapters.FirstOrDefault(c => c.Number == number && VolumeMayTake(c, volumeFileIds, replaceExisting, displaceable));
            if (match != null && !targets.Contains(match))
            {
                targets.Add(match);
            }
        }

        foreach (var chapter in targets)
        {
            chapter.ChapterFileId = chapterFileId;
            AdoptParsedVolume(chapter, parsed);
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
    /// Takes unlinked chapters and, when <paramref name="replaceExisting"/> allows, chapters on
    /// single-chapter files; never one already on another volume. Returns the number of chapters
    /// newly linked.
    /// </summary>
    private int FillVolumeContents(
        List<Chapter> chapters, IEnumerable<(int FileId, string AbsolutePath, ParsedReleaseFile Parsed)> files,
        HashSet<int> volumeFileIds, bool replaceExisting, IReadOnlySet<int>? displaceable = null)
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
                var chapter = chapters.FirstOrDefault(c => c.Number == number && c.ChapterFileId == null)
                              ?? chapters.FirstOrDefault(c =>
                                  c.Number == number && c.ChapterFileId != fileId &&
                                  VolumeMayTake(c, volumeFileIds, replaceExisting, displaceable));
                if (chapter != null)
                {
                    chapter.ChapterFileId = fileId;
                    AdoptParsedVolume(chapter, parsed);
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
    /// <param name="filePath">
    /// The file on disk; a single file's name also says its language. A volume whose page names
    /// carry chapter markers takes a chapter off another file only when the markers name it; the
    /// provider's range alone would hand a partial volume every chapter of that volume and orphan
    /// files it does not replace.
    /// </param>
    /// <param name="replaceExisting">
    /// False leaves a chapter that already has a file alone, so this file links only what nothing
    /// backs yet.
    /// </param>
    private static List<Chapter> LinkChapters(
        List<Chapter> chapters, ParsedReleaseFile parsed, int chapterFileId, string? filePath,
        HashSet<int> volumeFileIds, bool replaceExisting = true, IReadOnlySet<int>? displaceable = null)
    {
        List<Chapter> targets = [];
        if (parsed.IsChapter)
        {
            // A single file replaces another single file, never a volume.
            var languages = filePath is null
                ? null
                : ChapterFileLanguage.FromName(filePath, ChapterFileLanguage.SeriesLanguages(chapters));
            bool Fits(Chapter c) => c.Number == parsed.Number
                                    && (languages is null || languages.Contains(ChapterFileLanguage.Of(c)));
            var match = chapters.FirstOrDefault(c => Fits(c) && c.ChapterFileId == null)
                        ?? (replaceExisting
                            ? chapters.FirstOrDefault(c =>
                                Fits(c) && c.ChapterFileId is { } fileId &&
                                (displaceable?.Contains(fileId) ?? !volumeFileIds.Contains(fileId)))
                            : null);
            if (match != null)
            {
                targets.Add(match);
            }
        }
        else if (parsed.IsVolume)
        {
            var end = parsed.VolumeEnd ?? parsed.Volume;
            HashSet<decimal>? markers = null;
            targets = chapters
                .Where(c => c.Volume >= parsed.Volume && c.Volume <= end && c.ChapterFileId != chapterFileId
                            && VolumeMayTake(c, volumeFileIds, replaceExisting, displaceable))
                .Where(c =>
                {
                    if (c.ChapterFileId is null)
                    {
                        return true;
                    }

                    markers ??= filePath is null ? [] : [.. VolumeChapterScanner.ScanCbz(filePath)];
                    return markers.Count == 0 || (c.Number is { } number && markers.Contains(number));
                })
                .ToList();
        }

        foreach (var chapter in targets)
        {
            chapter.ChapterFileId = chapterFileId;
            AdoptParsedVolume(chapter, parsed);
        }

        return targets;
    }

    /// <summary>
    /// Copies the volume the file name carries onto a chapter that has none, so the naming tokens
    /// and the volume column can see it for a series no source maps to volumes. A chapter that
    /// already has one keeps it: the provider's assignment outranks a scene release's. A
    /// multi-volume compilation is skipped, since it says nothing about which of its volumes any
    /// one chapter belongs to.
    /// </summary>
    private static void AdoptParsedVolume(Chapter chapter, ParsedReleaseFile parsed)
    {
        if (parsed.VolumeEnd is null && parsed.Volume is int volume)
        {
            chapter.Volume ??= volume;
        }
    }
}
