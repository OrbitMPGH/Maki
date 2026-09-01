using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Naming;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/series")]
public class SeriesController(
    MakiDbContext db,
    CoverService coverService,
    ChapterSyncService chapterSyncService,
    CbzLinkService cbzLinkService,
    SeriesCreationService seriesCreation,
    SeriesRenameService seriesRename,
    SeriesMetadataRefreshService metadataRefresh,
    DownloadQueueService downloadQueue,
    DownloadBatchNotifier downloadBatches,
    IAppSettings appSettings,
    KavitaScanService kavitaScans,
    ScrobbleService scrobbler,
    StatsEventService stats,
    MangaBakaLocalStore mangaBakaStore,
    SimilarSeriesService similarSeries,
    ReaderArchiveCache archives,
    SourceAvailability sourceAvailability,
    ICurrentUser currentUser,
    ILogger<SeriesController> logger) : ControllerBase
{
    /// <summary>
    /// How many cards the "More like this" rail gets. A horizontal rail is scrolled, not paged, so
    /// this is the whole list — there is no "show more" behind it.
    /// </summary>
    private const int RailSize = 20;

    /// <summary>Re-pulls all metadata from the provider, including the poster image.</summary>
    [Authorize(Policy = Policies.EditMetadata)]
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

        return Ok(SeriesDto.FromEntity(series, rating: await RatingForAsync(id, ct)));
    }

    /// <summary>Re-standardizes the ComicInfo.xml inside every CBZ the series owns.</summary>
    [Authorize(Policy = Policies.EditMetadata)]
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
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("{id:int}/searchmissing")]
    public async Task<IActionResult> SearchMissing(int id, CancellationToken ct)
    {
        var title = await db.Series.Where(s => s.Id == id).Select(s => s.Title).FirstOrDefaultAsync(ct);
        if (title is null)
        {
            return NotFound();
        }

        var missing = await db.Chapters
            .Where(c => c.SeriesId == id && c.Monitored && c.ChapterFileId == null)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // Collected so the whole run notifies twice (queued, then a summary) instead of once per
        // chapter — adding a long series used to fire a ping for every chapter it downloaded.
        var queuedItemIds = new List<int>();
        foreach (var chapterId in missing)
        {
            try
            {
                if (await downloadQueue.EnqueueChapterAsync(
                        chapterId, ct, DownloadOrigin.Manual, currentUser.UserId) is { } item)
                {
                    queuedItemIds.Add(item.Id);
                }
            }
            catch (InvalidOperationException ex)
            {
                downloadBatches.Queued(id, title, queuedItemIds);
                return BadRequest(new { error = ex.Message, queued = queuedItemIds.Count });
            }
        }

        downloadBatches.Queued(id, title, queuedItemIds);
        return Ok(new { queued = queuedItemIds.Count });
    }

    [Authorize(Policy = Policies.EditMetadata)]
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
    [Authorize(Policy = Policies.EditMetadata)]
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
                Known = g.Count(),
            })
            .ToDictionaryAsync(x => x.SeriesId, ct);

        // Active download work per series, so cards can show "queued"/"downloading" at a glance.
        var queueCounts = await db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
                        q.Status != QueueStatus.Cancelled)
            .GroupBy(q => q.SeriesId)
            .Select(g => new
            {
                SeriesId = g.Key,
                Queued = g.Count(q => q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited),
                Downloading = g.Count(q => q.Status != QueueStatus.Queued && q.Status != QueueStatus.RateLimited),
            })
            .ToDictionaryAsync(x => x.SeriesId, ct);

        var readCounts = await ReadChapterCountsBySeriesAsync(ct);

        // Flat join-table read rather than Include(s => s.UserTags): the series above are already
        // materialized, and most libraries have far fewer tag links than series. Going through the
        // skip navigation instead would need SQL APPLY, which SQLite doesn't support.
        var tagIdsBySeries = (await db.SeriesTags.ToListAsync(ct))
            .GroupBy(x => x.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TagId).ToList());

        // One query for the caller's own ratings — the query filter narrows it to their rows, so a
        // shared library shows each reader their own score with no per-series lookup.
        var ratings = await db.UserSeriesStates
            .Where(x => x.Rating != null)
            .Select(x => new { x.SeriesId, x.Rating })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Rating, ct);

        // Which sources each series is linked to, and which of those actually run. Two flat reads
        // grouped in memory rather than Include(s => s.SourceMappings) on the materialized list
        // above: the same shape as the tag read, and one query instead of one per series.
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var mappingsBySeries = (await db.SourceMappings
                .Select(m => new { m.SeriesId, m.SourceName, m.Enabled })
                .ToListAsync(ct))
            .GroupBy(m => m.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Distinct in SQL: a series with 400 downloaded chapters otherwise drags 400 rows across
        // for what collapses to one or two names.
        var fileSourcesBySeries = (await db.ChapterFiles
                .Where(f => f.SourceName != "")
                .Select(f => new { f.SeriesId, f.SourceName })
                .Distinct()
                .ToListAsync(ct))
            .GroupBy(f => f.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.SourceName).Order().ToList());

        return Ok(series.Select(s =>
        {
            chapterCounts.TryGetValue(s.Id, out var counts);
            queueCounts.TryGetValue(s.Id, out var queue);
            // Nullable on purpose: absent means nothing has ever been read, which the UI hides
            // rather than drawing an empty "0 read" bar. `out var` would type this as int and
            // silently turn every untouched series into a reported zero.
            int? readCount = readCounts.TryGetValue(s.Id, out var read) ? read : null;
            var mappings = mappingsBySeries.GetValueOrDefault(s.Id) ?? [];
            return SeriesDto.FromEntity(
                s, counts?.Total ?? 0, counts?.WithFile ?? 0, counts?.Known ?? 0,
                queue?.Queued ?? 0, queue?.Downloading ?? 0, readCount,
                tagIdsBySeries.GetValueOrDefault(s.Id) ?? [],
                ratings.GetValueOrDefault(s.Id)) with
            {
                Sources = [.. mappings.Select(m => m.SourceName).Distinct().Order()],
                EnabledSources =
                [
                    .. mappings
                        .Where(m => m.Enabled && !disabledSources.Contains(m.SourceName, StringComparer.OrdinalIgnoreCase))
                        .Select(m => m.SourceName)
                        .Distinct()
                        .Order(),
                ],
                FileSources = fileSourcesBySeries.GetValueOrDefault(s.Id) ?? [],
            };
        }));
    }

    /// <summary>
    /// Per series, how many of its downloaded chapters are read — a straight count of completed
    /// <see cref="ChapterProgress"/> rows, which is the ground truth for read state from both
    /// sources (the built-in reader, and Kavita through <see cref="ExternalReadSyncService"/>).
    /// Series with no rows at all are absent from the result, so the UI can hide the stat rather
    /// than claiming "0 read".
    /// <para>
    /// Deliberately <b>not</b> derived from <see cref="ReadingState.MaxChapter"/> any more. That
    /// mark is forward-only and covers every chapter numbered below it, so a single stale or
    /// mis-attributed Kavita read left a series permanently reporting chapters read that had never
    /// been opened — and nothing could clear it, because the mark may not be lowered.
    /// </para>
    /// </summary>
    /// <summary>
    /// The caller's own score for a series, or null. Needed by every endpoint that hands back a
    /// <see cref="SeriesDto"/> after a mutation: the rating is no longer a column on the entity, so
    /// leaving it out would return null and blank the star rating in the client's cache.
    /// </summary>
    private async Task<int?> RatingForAsync(int seriesId, CancellationToken ct) =>
        await db.UserSeriesStates
            .Where(x => x.SeriesId == seriesId)
            .Select(x => x.Rating)
            .FirstOrDefaultAsync(ct);

    private async Task<Dictionary<int, int>> ReadChapterCountsBySeriesAsync(CancellationToken ct) =>
        await ReadCounts.Read(db)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);

    /// <summary>
    /// Lists the raw CBZ files in the series folder cross-referenced with the database:
    /// each file's import status (linked / unlinked / unrecognized / missing-from-disk)
    /// and, for every linked file, the chapter(s) it backs — so failed imports are
    /// visible and volume compilations show the chapters they were mapped to.
    /// </summary>
    [HttpGet("{id:int}/files")]
    public async Task<IActionResult> Files(int id, CancellationToken ct)
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

        var seriesDir = Path.Combine(series.RootFolder.Path, series.FolderName);
        var records = await db.ChapterFiles.Where(f => f.SeriesId == id).ToListAsync(ct);
        var chapters = await db.Chapters
            .Where(c => c.SeriesId == id && c.ChapterFileId != null)
            .Select(c => new { c.ChapterFileId, c.Number })
            .ToListAsync(ct);

        // chapter numbers linked to each ChapterFile, ascending
        var chaptersByFile = chapters
            .GroupBy(c => c.ChapterFileId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Where(c => c.Number != null)
                    .OrderBy(c => c.Number)
                    .Select(c => c.Number!.Value.ToString("0.###", CultureInfo.InvariantCulture))
                    .ToList());

        var onDisk = Directory.Exists(seriesDir)
            ? Directory.GetFiles(seriesDir, "*.cbz", SearchOption.AllDirectories)
            : [];
        var diskByRelPath = onDisk.ToDictionary(
            f => Path.Combine(series.FolderName, Path.GetRelativePath(seriesDir, f)),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        var files = new List<SeriesFileDto>();
        var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Files Maki has a record for (linked, unlinked, or missing-from-disk).
        foreach (var record in records)
        {
            seenRelPaths.Add(record.RelativePath);
            var present = diskByRelPath.TryGetValue(record.RelativePath, out var absPath);
            var parsed = ReleaseNameParser.ParseFileName(record.RelativePath);
            var mapped = chaptersByFile.GetValueOrDefault(record.Id, []);

            var status = !present ? "missing"
                : mapped.Count > 0 ? "linked"
                : parsed.IsRecognized ? "unlinked"
                : "unrecognized";

            files.Add(new SeriesFileDto(
                record.RelativePath,
                Path.GetFileName(record.RelativePath),
                present ? new FileInfo(absPath!).Length : record.Size,
                record.SourceName,
                present,
                status,
                ParsedLabel(parsed),
                parsed.IsVolume,
                mapped));
        }

        // 2. Files on disk with no record yet (never imported — a rescan would adopt them).
        foreach (var (relPath, absPath) in diskByRelPath)
        {
            if (seenRelPaths.Contains(relPath))
            {
                continue;
            }

            var parsed = ReleaseNameParser.ParseFileName(relPath);
            files.Add(new SeriesFileDto(
                relPath,
                Path.GetFileName(relPath),
                new FileInfo(absPath).Length,
                null,
                true,
                parsed.IsRecognized ? "unlinked" : "unrecognized",
                ParsedLabel(parsed),
                parsed.IsVolume,
                []));
        }

        return Ok(files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Deletes the given CBZ files from disk, removes their ChapterFile records, and
    /// unlinks every chapter that shared each file (volume CBZs back several chapters).
    /// </summary>
    [Authorize(Policy = Policies.DeleteSeries)]
    [HttpDelete("{id:int}/files")]
    public async Task<IActionResult> DeleteFiles(int id, [FromBody] string[] relativePaths, CancellationToken ct)
    {
        if (relativePaths.Length == 0)
            return BadRequest(new { error = "No files selected" });

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
            return NotFound();

        if (series.RootFolder is null)
            return BadRequest(new { error = "Series has no root folder" });

        var files = await db.ChapterFiles
            .Where(f => f.SeriesId == id && relativePaths.Contains(f.RelativePath))
            .ToListAsync(ct);

        if (files.Count == 0)
            return Ok(new { deleted = 0 });

        var fileIds = files.Select(f => f.Id).ToList();
        var linkedByFileId = (await db.Chapters
                .Where(c => c.ChapterFileId != null && fileIds.Contains(c.ChapterFileId.Value))
                .ToListAsync(ct))
            .ToLookup(c => c.ChapterFileId!.Value);

        var deleted = 0;
        var failed = 0;
        foreach (var file in files)
        {
            // Resolve, never a bare Combine: RelativePath is stored data, and a row that escapes the
            // root would have this delete an arbitrary file for whoever holds DeleteSeries.
            var absPath = LibraryPaths.Resolve(series.RootFolder.Path, file.RelativePath);
            if (absPath is null)
            {
                logger.LogWarning("Refusing to delete {File}: resolves outside {Root}",
                    file.RelativePath, series.RootFolder.Path);
                failed++;
                continue;
            }

            try
            {
                System.IO.File.Delete(absPath);
            }
            catch (DirectoryNotFoundException)
            {
                // Containing directory is already gone — the file is effectively deleted.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File locked or permission denied: leave the record and its chapter
                // links intact so a half-finished batch doesn't drift from disk state.
                logger.LogWarning(ex, "Could not delete {File}, skipping", file.RelativePath);
                failed++;
                continue;
            }

            foreach (var chapter in linkedByFileId[file.Id])
                chapter.ChapterFileId = null;

            archives.Invalidate(file.Id);
            db.ChapterFiles.Remove(file);
            deleted++;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { deleted, failed });
    }

    private static string? ParsedLabel(ParsedReleaseFile parsed)
    {
        if (parsed.IsChapter)
        {
            return $"Ch.{parsed.Number!.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        if (parsed.IsVolume)
        {
            return parsed.VolumeEnd is { } end && end != parsed.Volume
                ? $"Vol.{parsed.Volume}-{end}"
                : $"Vol.{parsed.Volume}";
        }

        return null;
    }

    /// <summary>
    /// Scrobble status for this series: which trackers it's synced to, the last chapter/volume
    /// pushed, and whether it needs review. The library series is linked to its Kavita
    /// counterpart the same way the sync engine matches (punctuation-normalized title / folder
    /// name), so this reflects exactly what scrobbling did for it — no extra state to maintain.
    /// </summary>
    [HttpGet("{id:int}/scrobble")]
    public async Task<IActionResult> Scrobble(int id, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        var keys = new[] { series.Title, series.FolderName }
            .Select(n => ScrobbleMatching.NormalizeTitle(n ?? ""))
            .Where(k => k.Length > 0)
            .ToHashSet();

        var states = await db.ScrobbleSyncStates.AsNoTracking().ToListAsync(ct);
        var unmatched = await db.ScrobbleUnmatched.AsNoTracking().ToListAsync(ct);
        var allMappings = await db.ScrobbleMappings.AsNoTracking().ToListAsync(ct);

        bool Matches(string title) => keys.Contains(ScrobbleMatching.NormalizeTitle(title));

        // Link this library series to its Kavita series by matching the stored title on any
        // scrobble row. Mappings count too (a review/manual match carries the title but may
        // have no sync state yet), so a just-resolved series is visible immediately.
        var kavitaIds = states.Where(s => Matches(s.Title)).Select(s => s.KavitaSeriesId)
            .Concat(unmatched.Where(u => Matches(u.Title)).Select(u => u.KavitaSeriesId))
            .Concat(allMappings.Where(m => m.Title.Length > 0 && Matches(m.Title)).Select(m => m.KavitaSeriesId))
            .ToHashSet();

        var kavitaConfigured =
            !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
            !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaApiKey, ct));

        // Nothing to show and no cost worth paying: skip the tracker auth probes entirely.
        if (!kavitaConfigured && kavitaIds.Count == 0)
        {
            return Ok(new SeriesScrobbleDto(false, false, null, []));
        }

        var mappings = allMappings.Where(m => kavitaIds.Contains(m.KavitaSeriesId)).ToList();

        var serviceDtos = new List<SeriesScrobbleServiceDto>();
        var anyConnected = false;
        foreach (var tracker in scrobbler.Trackers)
        {
            var connected = await tracker.ConfiguredAsync(ct) && await tracker.AuthenticatedAsync(currentUser.UserId, ct);
            anyConnected |= connected;

            var mapping = mappings.FirstOrDefault(m => m.Service == tracker.Name);
            var state = states.FirstOrDefault(
                s => s.Service == tracker.Name && kavitaIds.Contains(s.KavitaSeriesId));
            var review = unmatched.FirstOrDefault(
                u => u.Service == tracker.Name && kavitaIds.Contains(u.KavitaSeriesId));

            if (!connected && mapping is null && state is null && review is null)
            {
                continue;
            }

            var remoteId = mapping is { RemoteId.Length: > 0 } ? mapping.RemoteId : null;
            var candidates = review is null
                ? []
                : JsonSerializer.Deserialize<List<ScrobbleService.CandidateDto>>(review.CandidatesJson) ?? [];

            serviceDtos.Add(new SeriesScrobbleServiceDto(
                tracker.Name,
                tracker.Label,
                connected,
                remoteId,
                mapping?.Method,
                remoteId is null ? null : tracker.EntryUrl(remoteId),
                state?.Chapter ?? 0,
                state?.Volume ?? 0,
                state?.Status,
                state?.SyncedAt,
                state?.Error,
                review?.Reason,
                candidates));
        }

        return Ok(new SeriesScrobbleDto(
            kavitaConfigured && anyConnected,
            kavitaIds.Count > 0,
            kavitaIds.Count > 0 ? kavitaIds.Min() : null,
            serviceDtos));
    }

    /// <summary>
    /// MangaBaka-listed relations of this series (sequels/prequels/spin-offs/side stories/main
    /// story) that aren't already in the library — for the series page's "Related" rail. Reads
    /// straight from <see cref="MangaBakaLocalStore.GetRelatedAsync"/>, not the recommendation
    /// pool: that pool is a single cached slot shared with Discover's "Recommended" tab, and
    /// recomputing it (with its heavier genre/tag similarity scan) on every series page visit
    /// would thrash that cache. Empty when the series has no MangaBaka id or the local dump isn't
    /// available, rather than an error — this is a supplementary section, not a core one.
    /// </summary>
    [HttpGet("{id:int}/related")]
    public async Task<IActionResult> Related(int id, CancellationToken ct)
    {
        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.MangaBakaId is not int mangaBakaId || !await mangaBakaStore.IsAvailableAsync(ct))
        {
            return Ok(Array.Empty<MangaBakaRecommendation>());
        }

        var libraryIds = await db.Series
            .Where(s => s.MangaBakaId != null)
            .Select(s => (long)s.MangaBakaId!.Value)
            .ToListAsync(ct);
        var related = await mangaBakaStore.GetRelatedAsync(
            [mangaBakaId], new HashSet<long>(libraryIds), ContentRating.Allowed(currentUser.MaxContentRating), ct);
        return Ok(related);
    }

    /// <summary>
    /// Series that <em>feel</em> like this one, for the series page's "More like this" rail — the
    /// semantic recommender seeded by this series alone. Complements
    /// <see cref="Related"/>, which only knows relations MangaBaka has declared.
    /// <para>
    /// Goes through <see cref="SimilarSeriesService"/> rather than <c>RecommendationService</c> for
    /// the reason spelled out on <see cref="Related"/>, and for the extra one that a per-series pool
    /// wants its own key rather than a single shared slot. Empty (never an error) when the series has
    /// no MangaBaka id, the local dump isn't available, or the embedding index isn't built.
    /// </para>
    /// </summary>
    [HttpGet("{id:int}/similar")]
    public async Task<IActionResult> Similar(int id, CancellationToken ct)
    {
        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.MangaBakaId is not int mangaBakaId || !await mangaBakaStore.IsAvailableAsync(ct))
        {
            return Ok(Array.Empty<MangaBakaRecommendation>());
        }

        var pool = await similarSeries.GetAsync(
            mangaBakaId, ContentRating.Allowed(currentUser.MaxContentRating), ct);
        if (pool.Count == 0)
        {
            return Ok(Array.Empty<MangaBakaRecommendation>());
        }

        // The pool is cached without the library excluded, so that one entry serves every user (see
        // SimilarSeriesService.GetAsync). Owning is per person, so the strip happens here, through the
        // scoped query filter — a series in a root folder this caller can't see is not "owned" to them.
        var owned = await db.Series
            .Where(s => s.MangaBakaId != null)
            .Select(s => (long)s.MangaBakaId!.Value)
            .ToListAsync(ct);
        var ownedSet = new HashSet<long>(owned);
        return Ok(pool
            .Where(r => !long.TryParse(r.ProviderId, out var providerId) || !ownedSet.Contains(providerId))
            .Take(RailSize)
            .ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.UserTags).Include(s => s.RootFolder)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        // See List(): unmonitored, un-downloaded chapters don't count toward the total.
        var total = await db.Chapters.CountAsync(
            c => c.SeriesId == id && (c.Monitored || c.ChapterFileId != null), ct);
        var withFile = await db.Chapters.CountAsync(c => c.SeriesId == id && c.ChapterFileId != null, ct);
        var known = await db.Chapters.CountAsync(c => c.SeriesId == id, ct);
        var active = await db.DownloadQueue
            .Where(q => q.SeriesId == id && q.Status != QueueStatus.Completed &&
                        q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled)
            .ToListAsync(ct);
        var queued = active.Count(q => q.Status is QueueStatus.Queued or QueueStatus.RateLimited);

        // Null means nothing has been read yet, which the UI hides instead of drawing an empty bar.
        // Through ReadCounts so this page and the library grid can't disagree about what "read" is.
        var readRows = await ReadCounts.Read(db).CountAsync(p => p.SeriesId == id, ct);
        int? readCount = readRows > 0 ? readRows : null;

        return Ok(SeriesDto.FromEntity(
            series, total, withFile, known, queued, active.Count - queued, readCount,
            rating: await RatingForAsync(id, ct), isAdmin: currentUser.Has(MakiPermission.Admin)));
    }

    [Authorize(Policy = Policies.AddSeries)]
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddSeriesRequest request, CancellationToken ct)
    {
        // deferSourceMatching: the button is the whole point here. Matching every source and pulling
        // the first chapter list is tens of seconds of network; the caller gets the series row and
        // the Sources card shows a spinner until the background worker is done.
        var result = await seriesCreation.CreateAsync(
            request.MetadataProviderId, request.RootFolderId, request.Monitored, request.MonitorNewItems, ct,
            deferSourceMatching: true, incognito: request.Incognito);

        if (result.Series is null)
        {
            return result.Error switch
            {
                SeriesCreationError.RootFolderNotFound => BadRequest(new { error = "Root folder not found" }),
                SeriesCreationError.MetadataNotFound => BadRequest(new { error = "Series not found on metadata provider" }),
                _ => Conflict(new { error = "Series already exists in library" }),
            };
        }

        return CreatedAtAction(
            nameof(Get),
            new { id = result.Series.Id },
            SeriesDto.FromEntity(result.Series) with
            {
                Warnings = result.Warnings.Count > 0 ? result.Warnings : null
            });
    }

    [Authorize(Policy = Policies.DeleteSeries)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFiles, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.RootFolder != null)
        {
            var folder = Path.Combine(series.RootFolder.Path, series.FolderName);
            if (Directory.Exists(folder))
            {
                if (deleteFiles)
                {
                    Directory.Delete(folder, recursive: true);
                }
                else if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder, recursive: false);
                }
            }
        }

        // Snapshot before the hard delete: the event row must outlive the series (FK is severed
        // to NULL), so it carries the title, the genre/tag lists the aggregation needs later, and
        // the durable identity — without that last one the removal event would land under a
        // title-only key while the reads before it kept the provider key, splitting one history.
        var payload = JsonSerializer.Serialize(new { genres = series.Genres, tags = series.Tags });
        var title = series.Title;
        var seriesKey = SeriesIdentity.For(series);

        db.Series.Remove(series);
        await db.SaveChangesAsync(ct);
        coverService.DeleteCover(id);
        await stats.RecordAsync(
            StatsEventType.SeriesRemoved, null, title, payloadJson: payload, seriesKey: seriesKey, ct: ct);
        return NoContent();
    }

    /// <param name="MoveFiles">
    /// True: Maki moves the on-disk folder itself. False: the user already relocated the files
    /// (or is about to) — only <see cref="Series.RootFolderId"/> is repointed.
    /// </param>
    public record MoveSeriesRequest(int RootFolderId, bool MoveFiles = true);

    /// <summary>
    /// Relocates a series to a different root folder: repoints <see cref="Series.RootFolderId"/>
    /// and, when <see cref="MoveSeriesRequest.MoveFiles"/> is true, moves the on-disk folder too
    /// (same <see cref="Series.FolderName"/>, so every <see cref="ChapterFile.RelativePath"/>
    /// stays valid unchanged). Either way, re-triggers a Kavita scan of both the old location (so
    /// Kavita notices the files are gone) and the new one (so it picks them back up). A file move
    /// is refused while a download for this series is in flight — it writes into the old folder
    /// mid-move otherwise; a DB-only repoint isn't, since nothing on disk is touched.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/move")]
    public async Task<IActionResult> Move(int id, [FromBody] MoveSeriesRequest request, CancellationToken ct)
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

        if (request.RootFolderId == series.RootFolderId)
        {
            return BadRequest(new { error = "Series is already in that root folder" });
        }

        var destination = await db.RootFolders.FindAsync([request.RootFolderId], ct);
        if (destination is null)
        {
            return BadRequest(new { error = "Root folder not found" });
        }

        var oldFolder = Path.Combine(series.RootFolder.Path, series.FolderName);
        var newFolder = Path.Combine(destination.Path, series.FolderName);

        if (request.MoveFiles)
        {
            var active = await db.DownloadQueue.AnyAsync(q => q.SeriesId == id &&
                q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
                q.Status != QueueStatus.Cancelled, ct);
            if (active)
            {
                return Conflict(new { error = "Series has an active download — wait for it to finish before moving" });
            }

            if (Directory.Exists(newFolder))
            {
                return Conflict(new { error = $"Destination folder already exists: {newFolder}" });
            }

            if (Directory.Exists(oldFolder))
            {
                try
                {
                    MoveDirectory(oldFolder, newFolder);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not move series folder for {Title} to {Destination}", series.Title, destination.Path);
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        new { error = $"Could not move the series folder: {ex.Message}" });
                }
            }
        }
        else if (!Directory.Exists(newFolder))
        {
            return BadRequest(new
            {
                error = $"Files not moved: {newFolder} doesn't exist. Move the files there first, or let Maki move them."
            });
        }

        var oldRootFolderPath = series.RootFolder.Path;
        series.RootFolderId = destination.Id;
        await db.SaveChangesAsync(ct);

        kavitaScans.QueueScan(oldFolder, series.Id);
        kavitaScans.QueueScan(newFolder, series.Id);

        return Ok(SeriesDto.FromEntity(series, rating: await RatingForAsync(series.Id, ct)) with
        {
            Warnings = [$"Series folder moved from {oldRootFolderPath} to {destination.Path}"]
        });
    }

    public record RenameSeriesRequest(List<int> SeriesIds);

    /// <summary>
    /// What renaming this series to the current naming formats would move, without moving anything.
    /// A format change never touches files on its own, so this plus <see cref="Rename"/> is the
    /// only way an existing series adopts a new format.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpGet("{id:int}/rename/preview")]
    public async Task<IActionResult> RenamePreview(int id, CancellationToken ct)
    {
        var plan = await seriesRename.PlanAsync(id, ct);
        return plan is null ? NotFound() : Ok(plan);
    }

    /// <summary>
    /// Renames the series folder and every chapter file in it to match the current formats.
    /// Refused while a download for this series is in flight — it writes into the old folder
    /// halfway through — and when two chapters would end up sharing a file name.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, CancellationToken ct)
    {
        var result = await seriesRename.RenameAsync(id, ct);
        if (result.Error is null)
        {
            return Ok(result);
        }

        return result.Plan is null
            ? NotFound(new { error = result.Error })
            : Conflict(new { error = result.Error, warnings = result.Warnings });
    }

    /// <summary>
    /// Same rename, over a list. Each series is independent: one refusing (an active download, a
    /// name collision) doesn't stop the rest, and the per-series results say which did what.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("rename")]
    public async Task<IActionResult> RenameMany(
        [FromBody] RenameSeriesRequest request, CancellationToken ct) =>
        Ok(await seriesRename.RenameManyAsync(request.SeriesIds ?? [], ct));

    /// <summary>
    /// <see cref="Directory.Move"/> only works within one volume — root folders routinely live on
    /// different mounts/drives, so fall back to a recursive copy + delete when the direct move
    /// fails (cross-device rename).
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Likely cross-volume; fall through to copy+delete.
        }

        CopyDirectory(source, destination);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            System.IO.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    public record MonitorModeRequest(string Mode);

    /// <summary>Rating on a 1–10 scale, or null to clear it.</summary>
    public record SetRatingRequest(int? Rating);

    /// <summary>
    /// Applies a monitor mode (All / MainOnly / None) to every existing chapter and
    /// persists it as the mode for chapters that appear later.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
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
        if (mode != NewChapterMonitorMode.Smart)
        {
            foreach (var chapter in chapters)
            {
                chapter.Monitored = Chapter.MonitoredUnder(mode, chapter.Number);
            }
        }
        else
        {
            await SmartDownloadJob.MonitorSmart(chapters, appSettings, ct);
        }

        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            mode = mode.ToString(),
            monitored = chapters.Count(c => c.Monitored),
            total = chapters.Count
        });
    }

    public record IncognitoRequest(string Mode);

    /// <summary>
    /// Sets a series' <see cref="IncognitoMode"/>. "ScrobbleOnly" withholds tracker pushes only;
    /// "Full" also withholds it from Rewind/reading-history stats. Both are enforced at write
    /// time (<see cref="StatsEventService"/>, <see cref="ReadingProgressService"/>,
    /// <see cref="ScrobbleService"/>) — nothing needs to filter it back out on read.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/incognito")]
    public async Task<IActionResult> SetIncognito(int id, [FromBody] IncognitoRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<IncognitoMode>(request.Mode, true, out var mode))
        {
            return BadRequest(new { error = $"Unknown mode: {request.Mode}" });
        }

        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        series.Incognito = mode;
        await db.SaveChangesAsync(ct);
        return Ok(new { incognito = mode.ToString() });
    }

    /// <summary>
    /// Sets <em>this user's</em> rating (1–10, or null to clear) and best-effort pushes the score to
    /// the trackers <em>they</em> have connected. A tracker that isn't connected or can't be resolved
    /// is silently skipped.
    /// </summary>
    // Needs no permission beyond being signed in: the score lives in the caller's own
    // UserSeriesState row and is pushed to their own tracker accounts. It was briefly gated on
    // EditMetadata for exactly as long as it was a shared column on Series, where a reader-only
    // account could overwrite the admin's score on the admin's AniList profile.
    [HttpPut("{id:int}/rating")]
    public async Task<IActionResult> SetRating(int id, [FromBody] SetRatingRequest request, CancellationToken ct)
    {
        if (request.Rating is { } r && r is < 1 or > 10)
        {
            return BadRequest(new { error = "Rating must be between 1 and 10, or null to clear" });
        }

        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        var state = await db.UserSeriesStates.FirstOrDefaultAsync(s => s.SeriesId == id, ct);
        if (state is null)
        {
            state = new UserSeriesState { SeriesId = id };
            db.UserSeriesStates.Add(state);
        }

        state.Rating = request.Rating;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Push the score (0 clears it on trackers that support that) in the background — tracker
        // auth-checks + network + pacing take several seconds, and the UI shouldn't wait on them.
        // The scrobble log records what synced.
        scrobbler.QueueRatingPush(currentUser.UserId, series, request.Rating ?? 0);
        return Ok(new { rating = state.Rating });
    }

    /// <summary>
    /// Replaces the series' user tags with exactly the ids given. Tags themselves are created and
    /// deleted through <c>/api/v1/tags</c>; this only rewires the links.
    /// </summary>
    [Authorize(Policy = Policies.ManageTags)]
    [HttpPut("{id:int}/tags")]
    public async Task<IActionResult> SetTags(int id, [FromBody] SetSeriesTagsRequest request, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.UserTags).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        var wanted = request.TagIds.Distinct().ToList();
        var tags = await db.Tags.Where(t => wanted.Contains(t.Id)).ToListAsync(ct);
        if (tags.Count != wanted.Count)
        {
            return BadRequest(new { error = "One or more tag ids do not exist" });
        }

        series.UserTags.Clear();
        series.UserTags.AddRange(tags);
        await db.SaveChangesAsync(ct);
        return Ok(new { tagIds = series.UserTags.Select(t => t.Id).ToList() });
    }

    /// <summary>The "unmonitor specials" setting turns a requested All into MainOnly.</summary>
}
