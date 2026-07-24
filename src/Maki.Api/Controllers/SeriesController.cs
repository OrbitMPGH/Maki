using System.Globalization;
using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Naming;
using Maki.Core.Parsing;
using Maki.Core.Scrobbling;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/series")]
public class SeriesController(
    MakiDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    SourceMatchService sourceMatchService,
    ChapterSyncService chapterSyncService,
    CbzLinkService cbzLinkService,
    SeriesMetadataRefreshService metadataRefresh,
    DownloadQueueService downloadQueue,
    IAppSettings appSettings,
    KavitaScanService kavitaScans,
    ScrobbleService scrobbler,
    StatsEventService stats,
    MangaBakaLocalStore mangaBakaStore,
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

        return Ok(series.Select(s =>
        {
            chapterCounts.TryGetValue(s.Id, out var counts);
            queueCounts.TryGetValue(s.Id, out var queue);
            readCounts.TryGetValue(s.Id, out var readCount);
            return SeriesDto.FromEntity(
                s, counts?.Total ?? 0, counts?.WithFile ?? 0, counts?.Known ?? 0,
                queue?.Queued ?? 0, queue?.Downloading ?? 0, readCount);
        }));
    }

    /// <summary>
    /// Per series, how many of its downloaded chapters fall at or below the Rewind read
    /// high-water mark (<see cref="ReadingState.MaxChapter"/>). Null for series with no
    /// <see cref="ReadingState"/> row yet (Kavita/scrobble hasn't reported reading progress for
    /// them) so the UI can hide the stat rather than claiming "0 read". Computed in memory —
    /// one row per Kavita series and one row per downloaded chapter, both cheap at library scale.
    /// </summary>
    private async Task<Dictionary<int, int>> ReadChapterCountsBySeriesAsync(CancellationToken ct)
    {
        var latestStateBySeries = (await db.ReadingStates
                .Where(r => r.SeriesId != null)
                .ToListAsync(ct))
            .GroupBy(r => r.SeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt).First());
        if (latestStateBySeries.Count == 0)
        {
            return [];
        }

        var downloadedNumbersBySeries = (await db.Chapters
                .Where(c => c.ChapterFileId != null && c.Number != null)
                .Select(c => new { c.SeriesId, Number = c.Number!.Value })
                .ToListAsync(ct))
            .GroupBy(c => c.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Number).ToList());

        return latestStateBySeries.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var maxChapter = (decimal)Math.Floor(kv.Value.MaxChapter);
                return downloadedNumbersBySeries.TryGetValue(kv.Key, out var numbers)
                    ? numbers.Count(n => n <= maxChapter)
                    : 0;
            });
    }

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
            var connected = await tracker.ConfiguredAsync(ct) && await tracker.AuthenticatedAsync(ct);
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
        var related = await mangaBakaStore.GetRelatedAsync([mangaBakaId], new HashSet<long>(libraryIds), ct);
        return Ok(related);
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
        var known = await db.Chapters.CountAsync(c => c.SeriesId == id, ct);
        var active = await db.DownloadQueue
            .Where(q => q.SeriesId == id && q.Status != QueueStatus.Completed &&
                        q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled)
            .ToListAsync(ct);
        var queued = active.Count(q => q.Status is QueueStatus.Queued or QueueStatus.RateLimited);

        // See ReadChapterCountsBySeriesAsync: null means no reading progress reported yet.
        var readState = await db.ReadingStates
            .Where(r => r.SeriesId == id)
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        int? readCount = null;
        if (readState is not null)
        {
            var maxChapter = (decimal)Math.Floor(readState.MaxChapter);
            readCount = await db.Chapters.CountAsync(
                c => c.SeriesId == id && c.ChapterFileId != null && c.Number != null && c.Number <= maxChapter, ct);
        }

        return Ok(SeriesDto.FromEntity(series, total, withFile, known, queued, active.Count - queued, readCount));
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
            KitsuId = metadata.KitsuId,
            MangaUpdatesId = metadata.MangaUpdatesId,
            MangaDexUuid = metadata.MangaDexUuid,
            // Monitoring is only the mode now, so an unmonitored add is simply mode None —
            // there's no separate flag left for it to contradict.
            MonitorNewItems = await DefaultedMonitorMode(
                !request.Monitored
                    ? NewChapterMonitorMode.None
                    : Enum.TryParse<NewChapterMonitorMode>(request.MonitorNewItems, true, out var mode)
                        ? mode
                        : NewChapterMonitorMode.All, ct),
            RootFolderId = rootFolder.Id,
            FolderName = FileNameSanitizer.Sanitize(metadata.Title),
            TotalChapters = metadata.TotalChapters,
            TotalVolumes = metadata.TotalVolumes,
            AuthorStory = metadata.AuthorStory,
            AuthorArt = metadata.AuthorArt,
            HasAnime = metadata.HasAnime,
            AnimeName = metadata.AnimeName,
            AnimeStart = metadata.AnimeStart,
            AnimeEnd = metadata.AnimeEnd,
            Added = DateTime.UtcNow,
            LastMetadataRefresh = DateTime.UtcNow
        };

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
        await stats.RecordAsync(StatsEventType.SeriesAdded, series.Id, series.Title, ct: ct);

        // The series row is already committed, so these steps can't fail the request — but they
        // can't be swallowed either: a series with no folder on disk looks fine until a download
        // lands. Collect what went wrong and hand it back with the 201.
        var warnings = new List<string>();

        var seriesFolder = Path.Combine(rootFolder.Path, series.FolderName);
        try
        {
            Directory.CreateDirectory(seriesFolder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create series folder for {Title}", series.Title);
            warnings.Add($"Could not create the series folder ({seriesFolder}): {ex.Message}");
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
            warnings.Add($"Could not match sources automatically: {ex.Message}. Link a source manually from the series page.");
        }

        return CreatedAtAction(
            nameof(Get),
            new { id = series.Id },
            SeriesDto.FromEntity(series) with { Warnings = warnings.Count > 0 ? warnings : null });
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

        // Snapshot before the hard delete: the event row must outlive the series (FK is severed
        // to NULL), so it carries the title and the genre/tag lists Rewind aggregates later.
        var payload = JsonSerializer.Serialize(new { genres = series.Genres, tags = series.Tags });
        var title = series.Title;

        db.Series.Remove(series);
        await db.SaveChangesAsync(ct);
        await stats.RecordAsync(StatsEventType.SeriesRemoved, null, title, payloadJson: payload, ct: ct);
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

        return Ok(SeriesDto.FromEntity(series) with
        {
            Warnings = [$"Series folder moved from {oldRootFolderPath} to {destination.Path}"]
        });
    }

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

    /// <summary>
    /// Sets the user's rating (1–10, or null to clear) and best-effort pushes the score to every
    /// connected tracker. A tracker that isn't connected or can't be resolved is silently skipped;
    /// the response reports which ones actually synced.
    /// </summary>
    [HttpPut("{id:int}/rating")]
    public async Task<IActionResult> SetRating(int id, [FromBody] SetRatingRequest request, CancellationToken ct)
    {
        if (request.Rating is { } r && r is < 1 or > 10)
        {
            return BadRequest(new { error = "Rating must be between 1 and 10, or null to clear" });
        }

        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        series.Rating = request.Rating;
        await db.SaveChangesAsync(ct);

        // Push the score (0 clears it on trackers that support that) in the background — tracker
        // auth-checks + network + pacing take several seconds, and the UI shouldn't wait on them.
        // The scrobble log records what synced.
        scrobbler.QueueRatingPush(series, request.Rating ?? 0);
        return Ok(new { rating = series.Rating });
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
