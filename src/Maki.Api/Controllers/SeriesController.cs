using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Naming;
using Maki.Core.Notifications;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/series")]
public class SeriesController(
    ILocalizer localizer,
    MakiDbContext db,
    CoverService coverService,
    ChapterSyncService chapterSyncService,
    CbzLinkService cbzLinkService,
    FileRelinkPlanner relinkPlanner,
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
    RecommendationFeedbackService recommendationFeedback,
    ReadingProfileService readingProfiles,
    ReadingTimeEstimateService readingTimeEstimates,
    SourceAvailability sourceAvailability,
    ICurrentUser currentUser,
    IUserSettings userSettings,
    NotificationService notifications,
    IUserLocaleResolver locales,
    ILogger<SeriesController> logger) : ControllerBase
{
    private string? _titleLanguage;
    private bool _titleLanguageRead;

    /// <summary>
    /// The caller's preferred title language(s), read once per request. The library grid builds one
    /// <see cref="SeriesDto"/> per series and every one of them wants this, so reading it per DTO
    /// would be a settings query per row.
    /// </summary>
    private async Task<string?> TitleLanguageAsync(CancellationToken ct)
    {
        if (!_titleLanguageRead)
        {
            _titleLanguage = await userSettings.GetAsync(SettingKeys.UiTitleLanguage, ct);
            _titleLanguageRead = true;
        }

        return _titleLanguage;
    }

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
            return this.Fail(localizer, "error.series.metadataRefreshFailed");
        }

        await db.SaveChangesAsync(ct);
        if (series.RootFolder is { } rootFolder)
        {
            kavitaScans.QueuePush(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        }

        var refreshed = await UserStateForAsync(id, ct);
        return Ok(SeriesDto.FromEntity(
            series, rating: refreshed.Rating, notificationMode: refreshed.NotificationMode,
            titleLanguage: await TitleLanguageAsync(ct)));
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
            return this.Fail(localizer, "error.series.noRootFolder");
        }

        var (updated, total) = await cbzLinkService.UpdateComicInfoAsync(series, ct);
        return Ok(new { updated, total });
    }

    /// <summary>Queues downloads for every wanted chapter that has no file yet.</summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("{id:int}/searchmissing")]
    public Task<IActionResult> SearchMissing(int id, CancellationToken ct) =>
        QueueNextWantedAsync(id, int.MaxValue, ct);

    /// <summary>How many chapters to take; the series page offers 10/25 and a custom value.</summary>
    public record DownloadNextRequest(int Count);

    /// <summary>
    /// Queues the next <c>Count</c> wanted chapters that have no file yet, lowest number first.
    /// This is what replaces unticking chapters as the way to download a series a bit at a time.
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("{id:int}/download/next")]
    public Task<IActionResult> DownloadNext(int id, [FromBody] DownloadNextRequest request, CancellationToken ct) =>
        request.Count < 1
            ? Task.FromResult<IActionResult>(this.Fail(localizer, "error.series.invalidDownloadCount"))
            : QueueNextWantedAsync(id, request.Count, ct);

    /// <summary>
    /// Queues up to <paramref name="count"/> wanted, undownloaded chapters in chapter-number order.
    /// <para>
    /// "Download all wanted" and "download the next N" differ only by that count, and both go
    /// through <see cref="Chapter.NextWanted"/> — the same selector Smart top-ups use — so "next"
    /// means one thing however it was asked for. The ordering matters for the unbounded case too: it
    /// is what makes a bulk grab arrive in reading order rather than in whatever order the source
    /// happened to list, since queue position follows enqueue order.
    /// </para>
    /// </summary>
    private async Task<IActionResult> QueueNextWantedAsync(int id, int count, CancellationToken ct)
    {
        var title = await db.Series.Where(s => s.Id == id).Select(s => s.Title).FirstOrDefaultAsync(ct);
        if (title is null)
        {
            return NotFound();
        }

        var chapters = await db.Chapters.Where(c => c.SeriesId == id).ToListAsync(ct);
        return await QueueChaptersAsync(id, title, Chapter.NextWanted(chapters, count), ct);
    }

    /// <summary>
    /// Enqueues a set of chapters as one manual batch. Item ids are collected so the run notifies
    /// twice (queued, then a summary) instead of once per chapter — adding a long series used to
    /// fire a ping for every chapter it downloaded.
    /// </summary>
    private async Task<IActionResult> QueueChaptersAsync(
        int seriesId, string title, IReadOnlyList<int> chapterIds, CancellationToken ct)
    {
        var result = await downloadQueue.EnqueueChaptersAsync(chapterIds, DownloadOrigin.Manual, currentUser.UserId, ct);
        var queuedItemIds = result.Queued.Select(item => item.Id).ToList();
        await downloadBatches.QueuedAsync(seriesId, title, queuedItemIds);
        return result.Error is not null
            ? BadRequest(new { error = localizer.Get(result.Error), queued = queuedItemIds.Count })
            : Ok(new { queued = queuedItemIds.Count });
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
            return this.Fail(localizer, "error.series.noRootFolder");
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
        return result.RootUnavailable ? this.Fail(localizer, "error.series.rootUnavailable") : Ok(result);
    }

    /// <summary>
    /// Previews a volumes-first rebuild of the chapter-to-file map: which chapters would move to
    /// which file, and which single-chapter files would be left backing nothing. Read-only.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/relink/plan")]
    public async Task<IActionResult> RelinkPlan(int id, [FromBody] RelinkPlanRequest request, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.RootFolder is null)
        {
            return this.Fail(localizer, "error.series.noRootFolder");
        }

        return Ok(await relinkPlanner.PlanAsync(series, request.Options, ct));
    }

    /// <summary>
    /// Applies the volumes-first rebuild. The plan is recomputed here rather than taken from the
    /// client, so what gets applied is what is on disk now, but a superseded file is only deleted
    /// when the client also saw it as superseded. Deleting needs DeleteSeries on top of
    /// EditMetadata. Refused while a download for the series is in flight, since it can land a
    /// file mid-plan.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/relink")]
    public async Task<IActionResult> Relink(int id, [FromBody] RelinkRequest request, CancellationToken ct)
    {
        if (request.DeleteSuperseded && !currentUser.Has(MakiPermission.DeleteSeries))
        {
            return Forbid();
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.RootFolder is null)
        {
            return this.Fail(localizer, "error.series.noRootFolder");
        }

        if (await HasActiveDownloadAsync(id, ct))
        {
            return this.Conflict(localizer, "error.series.activeDownloadRelink");
        }

        return Ok(await relinkPlanner.ApplyAsync(
            series, request.Options, request.DeleteSuperseded, request.ConfirmedSuperseded ?? [], ct));
    }

    private Task<bool> HasActiveDownloadAsync(int seriesId, CancellationToken ct) =>
        db.DownloadQueue.AnyAsync(q => q.SeriesId == seriesId &&
            q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
            q.Status != QueueStatus.Cancelled, ct);

    public record RelinkPlanRequest(string[]? ExcludedPaths, int[]? PinnedChapterIds)
    {
        public RelinkOptions Options => new(ExcludedPaths ?? [], PinnedChapterIds ?? []);
    }

    /// <param name="ConfirmedSuperseded">The superseded paths from the plan the user confirmed.</param>
    public record RelinkRequest(
        string[]? ExcludedPaths, int[]? PinnedChapterIds, bool DeleteSuperseded, string[]? ConfirmedSuperseded = null)
        : RelinkPlanRequest(ExcludedPaths, PinnedChapterIds);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().OrderBy(s => s.SortTitle).ToListAsync(ct);
        var chapterCounts = await ChapterTalliesAsync(db.Chapters, ct);

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

        // Flat join-table read scoped to these series in SQL, since SeriesTags has no visibility filter of its own.
        var tagIdsBySeries = (await db.SeriesTags
                .Where(x => db.Series.Any(s => s.Id == x.SeriesId))
                .ToListAsync(ct))
            .GroupBy(x => x.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TagId).ToList());

        // One query for the caller's own per-series state — the query filter narrows it to their
        // rows, so a shared library shows each reader their own score and their own notification
        // mode with no per-series lookup. Unfiltered on Rating now that a row can exist for the
        // mode alone: filtering on it would report every muted-but-unrated series as Default.
        var userStates = await db.UserSeriesStates
            .Select(x => new { x.SeriesId, x.Rating, x.NotificationMode })
            .ToDictionaryAsync(x => x.SeriesId, x => x, ct);

        // Which sources each series is linked to, and which of those actually run. Two flat reads
        // grouped in memory rather than Include(s => s.SourceMappings) on the materialized list
        // above: the same shape as the tag read, and one query instead of one per series.
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var mappingsBySeries = (await db.SourceMappings
                .Select(m => new { m.SeriesId, m.SourceName, m.Enabled })
                .ToListAsync(ct))
            .GroupBy(m => m.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Grouped in SQL to one row per (series, source) before crossing the wire. The ChapterFile
        // query filter already scopes this to visible series, so no explicit Series check here.
        var fileSourcesBySeries = (await db.ChapterFiles
                .Where(f => f.SourceName != "")
                .GroupBy(f => new { f.SeriesId, f.SourceName })
                .Select(g => g.Key)
                .ToListAsync(ct))
            .GroupBy(f => f.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.SourceName).Order().ToList());

        var titleLanguage = await TitleLanguageAsync(ct);

        return Ok(series.Select(s =>
        {
            chapterCounts.TryGetValue(s.Id, out var counts);
            queueCounts.TryGetValue(s.Id, out var queue);
            // Nullable on purpose: absent means nothing has ever been read, which the UI hides
            // rather than drawing an empty "0 read" bar. `out var` would type this as int and
            // silently turn every untouched series into a reported zero.
            int? readCount = readCounts.TryGetValue(s.Id, out var read) ? read : null;
            var mappings = mappingsBySeries.GetValueOrDefault(s.Id) ?? [];
            var userState = userStates.GetValueOrDefault(s.Id);
            return SeriesDto.FromEntity(
                s, counts?.Wanted ?? 0, counts?.WithFile ?? 0, counts?.Known ?? 0,
                queue?.Queued ?? 0, queue?.Downloading ?? 0, readCount,
                tagIdsBySeries.GetValueOrDefault(s.Id) ?? [],
                userState?.Rating,
                notificationMode: userState?.NotificationMode ?? SeriesNotificationMode.Default,
                titleLanguage: titleLanguage) with
            {
                RemovedChapterCount = counts?.Removed ?? 0,
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

    private sealed record ChapterTallies(int SeriesId, int Wanted, int WithFile, int Known, int Removed);

    /// <summary>
    /// The chapter counts every series surface reports, keyed by series id. The list and detail
    /// endpoints must agree on these, so they share one expression rather than each spelling it out.
    /// <para>
    /// <c>Wanted</c> counts what the user asked for plus anything already on disk: a chapter they
    /// don't want and don't have (a skipped special) is excluded so a fully-downloaded series reads
    /// 39/39 rather than 39/40, while one they don't want but already have still counts on both
    /// sides. Chapters merely waiting to download are wanted, so deferring never shrinks this. A
    /// chapter whose file was removed on purpose counts too, so clearing read files never shrinks it.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<int, ChapterTallies>> ChapterTalliesAsync(
        IQueryable<Chapter> chapters, CancellationToken ct) =>
        await chapters
            .GroupBy(c => c.SeriesId)
            .Select(g => new ChapterTallies(
                g.Key,
                g.Count(c => c.Wanted || c.ChapterFileId != null || c.FileRemovedAt != null),
                g.Count(c => c.ChapterFileId != null),
                g.Count(),
                g.Count(c => c.ChapterFileId == null && c.FileRemovedAt != null)))
            .ToDictionaryAsync(x => x.SeriesId, ct);

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
    /// The caller's own per-series state: their score, and their notification mode. Needed by every
    /// endpoint that hands back a <see cref="SeriesDto"/> after a mutation — neither is a column on
    /// the entity any more, so leaving them out returns the defaults and blanks the star rating and
    /// the notification picker in the client's cache.
    /// </summary>
    private async Task<(int? Rating, SeriesNotificationMode NotificationMode)> UserStateForAsync(
        int seriesId, CancellationToken ct)
    {
        var state = await db.UserSeriesStates
            .Where(x => x.SeriesId == seriesId)
            .Select(x => new { x.Rating, x.NotificationMode })
            .FirstOrDefaultAsync(ct);

        return (state?.Rating, state?.NotificationMode ?? SeriesNotificationMode.Default);
    }

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
    public async Task<IActionResult> Files(int id, [FromServices] UpgradeEvaluationService upgrades,
        [FromServices] IMemoryCache cache, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (series.RootFolder is null)
        {
            return this.Fail(localizer, "error.series.noRootFolder");
        }

        var records = await db.ChapterFiles.Where(f => f.SeriesId == id).ToListAsync(ct);
        var chapters = await db.Chapters
            .Where(c => c.SeriesId == id && c.ChapterFileId != null)
            .Select(c => new { c.ChapterFileId, c.Number, c.Language })
            .ToListAsync(ct);
        var evaluator = await upgrades.ForSeriesAsync(id, ct);
        var languageByFile = chapters
            .GroupBy(c => c.ChapterFileId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Number is null).ThenBy(c => c.Number).First().Language);

        // chapter numbers linked to each ChapterFile, ascending
        var chaptersByFile = chapters
            .GroupBy(c => c.ChapterFileId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Where(c => c.Number != null)
                    .OrderBy(c => c.Number)
                    .Select(c => c.Number!.Value.ToString("0.###", CultureInfo.InvariantCulture))
                    .ToList());

        // Keyed by LibraryPaths.ComparisonKey so a row stored with the other OS's separator still
        // finds its file. Case-sensitive filesystems allow two files whose paths differ only in
        // case; they collapse to one entry here, so keep the first and don't throw.
        var diskByRelPath = new Dictionary<string, (string RelPath, string AbsPath)>(StringComparer.OrdinalIgnoreCase);
        var folders = await SeriesFolders.ForAsync(db, series, ct);
        foreach (var folder in folders)
        {
            var seriesDir = Path.Combine(series.RootFolder.Path, folder);
            if (!Directory.Exists(seriesDir))
            {
                continue;
            }

            foreach (var f in Directory.GetFiles(seriesDir, "*", SearchOption.AllDirectories)
                         .Where(ComicFile.IsComic).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relPath = Path.Combine(folder, Path.GetRelativePath(seriesDir, f));
                diskByRelPath.TryAdd(LibraryPaths.ComparisonKey(relPath), (relPath, f));
            }
        }

        SeriesFilesSummary.Remember(cache, id, series.RootFolder.Path, folders,
            records.Select(r => new SeriesFilesSummary.FileRecord(r.Id, r.RelativePath)), diskByRelPath.Keys);

        var files = new List<SeriesFileDto>();
        var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Files Maki has a record for (linked, unlinked, or missing-from-disk).
        foreach (var record in records)
        {
            var key = LibraryPaths.ComparisonKey(record.RelativePath);
            seenRelPaths.Add(key);
            var present = diskByRelPath.TryGetValue(key, out var disk);
            var absPath = disk.AbsPath;
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
                mapped,
                parsed.Number ?? (decimal?)parsed.Volume,
                evaluator?.Quality(record, languageByFile.GetValueOrDefault(record.Id)) ?? ChapterFileQualityDto.From(record)));
        }

        // 2. Files on disk with no record yet (never imported — a rescan would adopt them).
        foreach (var (key, (relPath, absPath)) in diskByRelPath)
        {
            if (seenRelPaths.Contains(key))
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
                [],
                parsed.Number ?? (decimal?)parsed.Volume,
                null));
        }

        return Ok(files
            .OrderBy(f => f.SortKey is null)
            .ThenBy(f => f.SortKey)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The Files tab count and the unlinked-files banner, so opening a series does not need the full
    /// listing above. See <see cref="SeriesFilesSummary"/> for how its folder walk is cached.
    /// </summary>
    [HttpGet("{id:int}/files/summary")]
    public async Task<IActionResult> FilesSummary(int id, [FromServices] IMemoryCache cache, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id, ct))
        {
            return NotFound();
        }

        var summary = await SeriesFilesSummary.ForAsync(db, cache, id, ct);
        return summary is null ? this.Fail(localizer, "error.series.noRootFolder") : Ok(summary);
    }

    /// <summary>
    /// Deletes the given files from disk and removes their records. Every chapter that shared one
    /// (volume CBZs back several) is marked removed rather than left missing, so it keeps its reads
    /// and nothing downloads it again; see <see cref="ChapterFileDeletion"/>.
    /// </summary>
    [Authorize(Policy = Policies.DeleteSeries)]
    [HttpDelete("{id:int}/files")]
    public async Task<IActionResult> DeleteFiles(
        int id, [FromBody] string[] relativePaths, [FromServices] ChapterFileDeletion deletion, CancellationToken ct)
    {
        if (relativePaths.Length == 0)
            return this.Fail(localizer, "error.series.noFilesSelected");

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
            return NotFound();

        if (series.RootFolder is null)
            return this.Fail(localizer, "error.series.noRootFolder");

        // A rescan or import holding the lock would otherwise link to, or save over, a row removed here.
        using var seriesLock = await SeriesLocks.SeriesAsync(id, ct);
        var files = await db.ChapterFiles
            .Where(f => f.SeriesId == id && relativePaths.Contains(f.RelativePath))
            .ToListAsync(ct);

        // The Files tab also lists comics on disk that have no record (never adopted), and those
        // are what this dialog is most often used to clean up.
        var (strayDeleted, strayFailed) = await DeleteStrayFilesAsync(
            series, relativePaths.Except(files.Select(f => f.RelativePath), StringComparer.Ordinal).ToList(),
            deletion, ct);

        // A file another series still points at stays on disk, but this series has let go of it,
        // which is what the user asked for here, so it reads as deleted.
        var result = await deletion.DeleteAsync(series, files, ct);
        return Ok(new { deleted = strayDeleted + result.Deleted + result.Kept, failed = strayFailed + result.Failed });
    }

    /// <summary>
    /// Deletes requested paths that have no record: only a comic, only inside one of this series'
    /// folders, never through a link, and never a path another series has a record for.
    /// </summary>
    private async Task<(int Deleted, int Failed)> DeleteStrayFilesAsync(
        Series series, List<string> relativePaths, ChapterFileDeletion deletion, CancellationToken ct)
    {
        if (relativePaths.Count == 0)
        {
            return (0, 0);
        }

        var rootPath = series.RootFolder!.Path;
        var folders = await SeriesFolders.ForAsync(db, series, ct);
        // Any record anywhere counts, including one under a nested root folder or in a root the
        // caller cannot see: that file is somebody's chapter, not a stray.
        var recorded = await deletion.ClaimedAsync(
            relativePaths
                .Select(p => LibraryPaths.ResolveForDelete(rootPath, LibraryPaths.ComparisonKey(p)))
                .OfType<string>()
                .ToList(),
            new HashSet<int>(),
            ct);

        var deleted = 0;
        var failed = 0;
        foreach (var path in relativePaths)
        {
            var key = LibraryPaths.ComparisonKey(path);
            var absolute = LibraryPaths.ResolveForDelete(rootPath, key);
            if (absolute is null || recorded.Contains(absolute) || !ComicFile.IsComic(absolute) ||
                LibraryPaths.TopFolder(key) is not { } top || !folders.Contains(top, LibraryPaths.FolderComparer) ||
                !System.IO.File.Exists(absolute))
            {
                logger.LogWarning("Refusing to delete {File}: not an unrecorded comic in the folders of series {SeriesId}",
                    path, series.Id);
                failed++;
                continue;
            }

            try
            {
                System.IO.File.Delete(absolute);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {File}, skipping", path);
                failed++;
            }
        }

        return (deleted, failed);
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
        var suppressed = await recommendationFeedback.SuppressedAsync(currentUser.UserId, ct);
        return Ok(related.Where(r => !long.TryParse(r.ProviderId, out var providerId) || !suppressed.Contains(providerId)).ToList());
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
        var suppressed = await recommendationFeedback.SuppressedAsync(currentUser.UserId, ct);
        return Ok(pool
            .Where(r => !long.TryParse(r.ProviderId, out var providerId) ||
                !ownedSet.Contains(providerId) && !suppressed.Contains(providerId))
            .Take(RailSize)
            .ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, [FromServices] ReadFileCleanupService readFileCleanup, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.UserTags).Include(s => s.RootFolder)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        var tallies = await ChapterTalliesAsync(db.Chapters.Where(c => c.SeriesId == id), ct);
        tallies.TryGetValue(id, out var counts);
        var (total, withFile, known) = (counts?.Wanted ?? 0, counts?.WithFile ?? 0, counts?.Known ?? 0);
        var active = await db.DownloadQueue
            .Where(q => q.SeriesId == id && q.Status != QueueStatus.Completed &&
                        q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled)
            .ToListAsync(ct);
        var queued = active.Count(q => q.Status is QueueStatus.Queued or QueueStatus.RateLimited);

        // Null means nothing has been read yet, which the UI hides instead of drawing an empty bar.
        // Through ReadCounts so this page and the library grid can't disagree about what "read" is.
        var readRows = await ReadCounts.Read(db).CountAsync(p => p.SeriesId == id, ct);
        int? readCount = readRows > 0 ? readRows : null;

        var readerPrefs = await readingProfiles.ResolveAsync(id, ct);
        // A profile or series override is an explicit reading-style choice. With only the global
        // default, use the format's conventional style so an unconfigured manhua/manhwa does not
        // borrow a manga pace merely because the application default is paged.
        var estimateMode = readerPrefs.Source == ReaderPrefsSource.Global &&
                           series.Type is SeriesTypes.Manhua or SeriesTypes.Manhwa
            ? Maki.Core.Reading.ReaderPrefsSpec.ModeVertical
            : readerPrefs.Prefs.Mode;
        var estimateTotal = Math.Max(series.TotalChapters ?? 0, known);
        var estimate = await readingTimeEstimates.EstimateAsync(
            id, estimateTotal, readRows, estimateMode, ct);

        var userState = await UserStateForAsync(id, ct);
        var pendingProposalId = await db.TorrentProposals
            .Where(p => p.SeriesId == id && p.Status == TorrentProposalStatus.Pending)
            .OrderByDescending(p => p.CreatedAtUtc)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
        var dto = SeriesDto.FromEntity(
            series, total, withFile, known, queued, active.Count - queued, readCount,
            rating: userState.Rating, isAdmin: currentUser.Has(MakiPermission.Admin),
            notificationMode: userState.NotificationMode,
            titleLanguage: await TitleLanguageAsync(ct)) with
        {
            RemovedChapterCount = counts?.Removed ?? 0,
            ReadFileCleanupActive = ReadFileCleanupService.AppliesTo(series.ReadFileCleanup, await readFileCleanup.OptionsAsync(ct)),
            PendingProposalId = pendingProposalId,
            ReadTimeEstimate = estimate is null
                ? null
                : new ReadingTimeEstimateDto(
                    estimate.Seconds,
                    estimate.RemainingChapters,
                    estimate.Style,
                    estimate.SampleChapters,
                    estimate.SeriesSpecific)
        };
        return Ok(dto);
    }

    [Authorize(Policy = Policies.AddSeries)]
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddSeriesRequest request, CancellationToken ct)
    {
        if (request.AddedFrom is not null and not ("library" or "recommendation"))
            return this.Fail(localizer, "error.series.unsupportedAddOrigin");
        // `is not { } id` and not `== Guid.Empty`: ClientMutationId is nullable, so the lifted
        // comparison is false for an absent field and the guard only ever caught a client that sent
        // all-zeros on purpose. An add with no id takes neither the receipt lookup nor the receipt
        // write in SeriesCreationService, so a retried request is a second series.
        if (request.ClientMutationId is not { } clientMutationId || clientMutationId == Guid.Empty)
            return this.Fail(localizer, "error.series.mutationIdRequired");
        if (request.UpgradeProfileId is { } profileId && !await db.UpgradeProfiles.AnyAsync(p => p.Id == profileId, ct))
            return this.Fail(localizer, "error.upgrades.profileNotFound");
        // deferSourceMatching: the button is the whole point here. Matching every source and pulling
        // the first chapter list is tens of seconds of network; the caller gets the series row and
        // the Sources card shows a spinner until the background worker is done.
        var result = await seriesCreation.CreateAsync(
            request.MetadataProviderId, request.RootFolderId, request.Monitored, request.MonitorNewItems, ct,
            deferSourceMatching: true, incognito: request.Incognito,
            attributedUserId: currentUser.UserId, addedFrom: request.AddedFrom,
            clientMutationId: clientMutationId, upgradeProfileId: request.UpgradeProfileId);

        if (result.Series is null)
        {
            return result.Error switch
            {
                SeriesCreationError.RootFolderNotFound => this.Fail(localizer, "error.series.rootFolderNotFound"),
                SeriesCreationError.MetadataNotFound => this.Fail(localizer, "error.series.metadataNotFound"),
                SeriesCreationError.MutationIdReused => this.Conflict(localizer, "error.series.mutationIdReused"),
                // 410, not 409: the operation really did happen, and its result is the thing that is
                // gone. Answering "already in library" would send the caller looking for a series
                // that is not there.
                SeriesCreationError.OperationResultGone => this.Gone(localizer, "error.series.addResultGone"),
                _ => this.Conflict(localizer, "error.series.alreadyExists"),
            };
        }

        var response = SeriesDto.FromEntity(result.Series, titleLanguage: await TitleLanguageAsync(ct)) with
              {
                  Warnings = result.Warnings.Count > 0 ? result.Warnings : null,
                  Operation = new SeriesOperationDto(
                      clientMutationId,
                      result.Warnings.Count > 0 ? "committed-with-warnings" :
                          result.Series.SourceMatchPending ? "setup-pending" : "committed",
                      result.Series.Id,
                      await db.RecommendationProfileStates
                          .Where(x => x.UserId == currentUser.UserId)
                          .Select(x => x.SignalRevision).FirstOrDefaultAsync(ct))
              };
        return result.Replayed && result.Series.SourceMatchPending
            ? AcceptedAtAction(nameof(Get), new { id = result.Series.Id }, response)
            : CreatedAtAction(nameof(Get), new { id = result.Series.Id }, response);
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

        using var seriesLock = await SeriesLocks.SeriesAsync(id, ct);

        // A worker holding one of its downloads would recreate the folder and drop a file into it
        // after the row is gone.
        if (await SeriesLocks.InFlight(db.DownloadQueue).AnyAsync(q => q.SeriesId == id, ct))
        {
            return this.Conflict(localizer, "error.series.activeDownloadDelete");
        }

        // Worked out while the rows that say what to delete still exist, and carried out after the
        // row is gone: a locked file must not leave half a folder deleted behind a series that is
        // still listed.
        var diskPlan = series.RootFolder is null ? null : await PlanSeriesDiskDeleteAsync(series, deleteFiles, ct);

        // Snapshot before the hard delete: the event row must outlive the series (FK is severed
        // to NULL), so it carries the title, the genre/tag lists the aggregation needs later, and
        // enough provider metadata for the stats feed to reopen it in Discover.
        string? coverUrl = null;
        if (series.MangaBakaId is int mangaBakaId && await mangaBakaStore.IsAvailableAsync(ct))
        {
            try
            {
                coverUrl = (await mangaBakaStore.GetDetailAsync(mangaBakaId, ct))?.CoverUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not snapshot the provider cover for removed series {SeriesId}", id);
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            genres = series.Genres,
            tags = series.Tags,
            providerId = series.MangaBakaId?.ToString(CultureInfo.InvariantCulture),
            coverUrl,
            rootFolderId = series.RootFolderId
        });
        var title = series.Title;
        var seriesKey = SeriesIdentity.For(series);
        // The join rows cascade away with the series, and tag-scoped connections still need them.
        var tagIds = await db.SeriesTags.Where(st => st.SeriesId == id).Select(st => st.TagId).ToListAsync(ct);

        // Before the delete cascades the provenance rows away, while they can still say whose
        // recommendation inputs this series was part of. Incremented in the database rather than on
        // tracked entities: these are other people's counters, and another request of theirs may be
        // advancing them at the same time.
        var provenanceOwners = await db.UserSeriesStates.IgnoreQueryFilters()
            .Where(x => x.SeriesId == id && x.AddedToLibraryAtUtc != null)
            .Select(x => x.UserId).Distinct().ToListAsync(ct);
        foreach (var owner in provenanceOwners)
        {
            await RecommendationFeedbackService.BumpAsync(db, owner, feedback: false, signal: true, ct);
        }
        db.Series.Remove(series);
        await db.SaveChangesAsync(ct);
        if (diskPlan is not null)
        {
            DeleteSeriesFromDisk(diskPlan, id);
        }

        // Still on somebody's tracker list, so an import list would add it straight back otherwise.
        if (series.MangaBakaId is int removedMangaBakaId)
        {
            await ImportListService.MarkRemovedAsync(db, removedMangaBakaId, ct);
        }
        coverService.DeleteCover(id);
        await stats.RecordAsync(
            StatsEventType.SeriesRemoved, null, title, payloadJson: payload, seriesKey: seriesKey, ct: ct);

        // No SeriesId or link: both would point at a row that no longer exists.
        var locale = await locales.DefaultAsync(ct);
        notifications.Dispatch(NotificationEventType.SeriesRemoved, new NotificationMessage(
            NotificationEventType.SeriesRemoved,
            Title: localizer.GetFor(locale, "notify.series.removed.title"),
            Body: localizer.GetFor(locale, "notify.series.removed.body", new
            {
                series = title,
                filesDeleted = deleteFiles ? "yes" : "no",
            }),
            SeriesTitle: title,
            SeriesTagIds: tagIds));
        return NoContent();
    }

    /// <param name="Folder">The series' own folder, when it resolves without passing a link.</param>
    /// <param name="RemoveFolderWhole">Delete <paramref name="Folder"/> recursively.</param>
    /// <param name="Files">Single files to delete, for folders the series does not own outright.</param>
    /// <param name="PruneIfEmpty">Folders removed only if nothing is left in them.</param>
    private sealed record SeriesDiskPlan(
        string? Folder, bool RemoveFolderWhole, List<string> Files, List<string> PruneIfEmpty, string? Trash);

    private async Task<SeriesDiskPlan> PlanSeriesDiskDeleteAsync(Series series, bool deleteFiles, CancellationToken ct)
    {
        var rootPath = series.RootFolder!.Path;
        var folder = LibraryPaths.ResolveNoLinks(rootPath, series.FolderName);
        if (folder is null && LibraryPaths.Resolve(rootPath, series.FolderName) is { } linked
            && Directory.Exists(linked))
        {
            logger.LogWarning("Leaving {Folder} on disk: it is or sits under a symbolic link or junction", linked);
        }

        // Older data can give two series one folder. A recursive delete of it would take the other
        // series' files too, so then only the files this series tracks go. Case is ignored on every
        // host, since a case-insensitive share under Docker holds "One Piece" and "one piece" as one.
        var shared = (await SeriesCreationService.SeriesFoldersInRootAsync(db, series.RootFolderId, series.Id, ct))
            .Contains(series.FolderName);
        if (!shared)
        {
            var pattern = series.FolderName.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            shared = (await db.ChapterFiles
                    .Where(f => f.SeriesId != series.Id &&
                                db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == series.RootFolderId) &&
                                EF.Functions.Like(f.RelativePath, pattern, @"\"))
                    .Select(f => f.RelativePath)
                    .ToListAsync(ct))
                .Any(p => LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(p), series.FolderName));
        }

        if (shared)
        {
            logger.LogWarning("Folder {Folder} is shared with another series; deleting only the files series {SeriesId} tracks",
                series.FolderName, series.Id);
        }

        // Folders this series only has some files in (a keep-new-standard import's original
        // folder). Only the files it tracks go, never the whole folder.
        var extraFolders = (await SeriesFolders.ForAsync(db, series, ct)).Skip(1).ToList();
        var partialFolders = shared ? extraFolders.Append(series.FolderName).ToList() : extraFolders;
        var files = new List<string>();
        if (deleteFiles)
        {
            var paths = await db.ChapterFiles.Where(f => f.SeriesId == series.Id)
                .Select(f => f.RelativePath).ToListAsync(ct);
            files.AddRange(paths
                .Where(p => LibraryPaths.TopFolder(p) is { } top && partialFolders.Contains(top, LibraryPaths.FolderComparer))
                .Select(p => LibraryPaths.ResolveForDelete(rootPath, LibraryPaths.ComparisonKey(p)))
                .OfType<string>());
        }

        var removeWhole = deleteFiles && !shared && folder is not null;
        var prune = extraFolders
            .Select(extra => LibraryPaths.ResolveNoLinks(rootPath, extra))
            .OfType<string>()
            .ToList();
        if (!removeWhole && folder is not null)
        {
            prune.Add(folder);
        }

        // Replaced copies from upgrades, and a volume import's superseded originals. Their history
        // rows go with the series, so nothing could restore them afterwards; but they are still the
        // user's files, so "keep files" leaves them for the trash purge to age out.
        var trash = UpgradeTrash.SeriesFolder(rootPath, series.Id);
        if (!deleteFiles)
        {
            prune.Add(trash);
        }

        return new SeriesDiskPlan(folder, removeWhole, files, prune, deleteFiles ? trash : null);
    }

    /// <summary>
    /// Best effort: the row is already gone, so a file that is locked or a folder that cannot be
    /// read is logged and left for Health's unlinked scan rather than failing the request.
    /// </summary>
    private void DeleteSeriesFromDisk(SeriesDiskPlan plan, int seriesId)
    {
        void Attempt(string path, Action delete)
        {
            try
            {
                delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {Path} for removed series {SeriesId}", path, seriesId);
            }
        }

        if (plan is { RemoveFolderWhole: true, Folder: { } folder } && Directory.Exists(folder))
        {
            Attempt(folder, () => Directory.Delete(folder, recursive: true));
        }

        foreach (var file in plan.Files)
        {
            if (System.IO.File.Exists(file))
            {
                Attempt(file, () => System.IO.File.Delete(file));
            }
        }

        foreach (var dir in plan.PruneIfEmpty)
        {
            Attempt(dir, () =>
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, recursive: false);
                }
            });
        }

        if (plan.Trash is { } trash && Directory.Exists(trash))
        {
            Attempt(trash, () => Directory.Delete(trash, recursive: true));
        }
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
            return this.Fail(localizer, "error.series.noRootFolder");
        }

        if (request.RootFolderId == series.RootFolderId)
        {
            return this.Fail(localizer, "error.series.alreadyInRootFolder");
        }

        using var seriesLock = await SeriesLocks.SeriesAsync(id, ct);

        var destination = await db.RootFolders.FindAsync([request.RootFolderId], ct);
        if (destination is null)
        {
            return this.Fail(localizer, "error.series.rootFolderNotFound");
        }

        if (!currentUser.AllRootFolders && !currentUser.RootFolderIds.Contains(request.RootFolderId))
        {
            return this.Fail(localizer, "error.series.rootFolderNotFound");
        }

        // Every folder the series has files in moves with it, or the stored paths of a
        // keep-new-standard import's original folder would point into the old root.
        var folders = await SeriesFolders.ForAsync(db, series, ct);
        var newFolder = Path.Combine(destination.Path, series.FolderName);

        // A failure part way through puts back whatever already moved, since RootFolderId is
        // not updated and would otherwise resolve it under the old root. The series' own folder
        // moves whole; a folder it only has some files in gives up just those files, the same
        // line Delete draws, so anything else living there stays put.
        var undo = new List<(string From, string To, bool IsDirectory)>();
        var emptiedFolders = new List<string>();
        // Directories the partial-folder branch below created for a single file's destination,
        // deepest first. A failed later file rolls the moved files back but leaves these behind;
        // left in place, the next attempt's Directory.Exists(target) pre-check sees a directory
        // that already exists and fails with error.series.destinationExists.
        var createdDirs = new List<string>();

        void CreateDirectoryTracked(string path)
        {
            var dir = path;
            while (!Directory.Exists(dir))
            {
                createdDirs.Add(dir);
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir)
                {
                    break;
                }

                dir = parent;
            }

            Directory.CreateDirectory(path);
        }

        void RollbackMoves()
        {
            for (var i = undo.Count - 1; i >= 0; i--)
            {
                var (from, to, isDirectory) = undo[i];
                try
                {
                    if (isDirectory)
                    {
                        MoveDirectory(to, from);
                    }
                    else
                    {
                        System.IO.File.Move(to, from);
                    }
                }
                catch (Exception rollbackEx)
                {
                    logger.LogError(rollbackEx, "Could not move {Path} back to {Root} after a failed series move",
                        from, series.RootFolder.Path);
                }
            }

            foreach (var dir in createdDirs)
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }
                catch (Exception rollbackEx)
                {
                    logger.LogError(rollbackEx, "Could not remove {Path} after a failed series move", dir);
                }
            }
        }

        if (request.MoveFiles)
        {
            if (await HasActiveDownloadAsync(id, ct))
            {
                return this.Conflict(localizer, "error.series.activeDownloadMove");
            }

            var trackedPaths = await db.ChapterFiles.Where(f => f.SeriesId == id)
                .Select(f => f.RelativePath).ToListAsync(ct);
            var sourceRoot = series.RootFolder.Path;
            bool TrackedPathTraversesLink(string folder) => trackedPaths
                .Where(p => LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(p), folder))
                .Any(p => LibraryPaths.Resolve(sourceRoot, LibraryPaths.ComparisonKey(p)) is { } path
                    && LibraryPaths.TraversesLink(sourceRoot, path));

            foreach (var (folder, index) in folders.Select((f, i) => (f, i)))
            {
                var target = Path.Combine(destination.Path, folder);
                if (Directory.Exists(target))
                {
                    return this.Conflict(localizer, "error.series.destinationExists", new { folder = target });
                }

                // The cross-volume fallback copies and then deletes the source, so a link anywhere in
                // the tree would either pull outside files into the library or be dropped along with
                // whatever rows reach through it.
                if (Directory.Exists(Path.Combine(series.RootFolder.Path, folder))
                    && (LibraryPaths.ResolveNoLinks(series.RootFolder.Path, folder) is not { } sourceFolder
                        || (index == 0 ? LibraryPaths.ContainsLink(sourceFolder) : TrackedPathTraversesLink(folder))))
                {
                    return this.Fail(localizer, "error.series.folderContainsLinks", new { folder });
                }
            }

            foreach (var (folder, index) in folders.Select((f, i) => (f, i)))
            {
                var source = Path.Combine(series.RootFolder.Path, folder);
                if (!Directory.Exists(source))
                {
                    continue;
                }

                try
                {
                    if (index == 0)
                    {
                        var target = Path.Combine(destination.Path, folder);
                        MoveDirectory(source, target);
                        undo.Add((source, target, true));
                        continue;
                    }

                    foreach (var path in trackedPaths.Where(p => LibraryPaths.FolderComparer.Equals(LibraryPaths.TopFolder(p), folder)))
                    {
                        var key = LibraryPaths.ComparisonKey(path);
                        if (LibraryPaths.ResolveNoLinks(series.RootFolder.Path, key) is not { } from
                            || LibraryPaths.ResolveNoLinks(destination.Path, key) is not { } to
                            || !System.IO.File.Exists(from))
                        {
                            continue;
                        }

                        CreateDirectoryTracked(Path.GetDirectoryName(to)!);
                        System.IO.File.Move(from, to);
                        undo.Add((from, to, false));
                    }

                    emptiedFolders.Add(source);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not move series folder for {Title} to {Destination}", series.Title, destination.Path);
                    RollbackMoves();

                    return StatusCode(StatusCodes.Status500InternalServerError,
                        new { error = $"Could not move the series folder: {ex.Message}" });
                }
            }
        }
        else
        {
            if (!folders.Any(f => Directory.Exists(Path.Combine(destination.Path, f))))
            {
                return this.Fail(localizer, "error.series.filesNotMoved", new { folder = newFolder });
            }

            // A series can span several folders. Repointing it while one is still under the old root
            // strands those chapters, so every tracked file still sitting there must also be present
            // at the destination. A file missing from both was already gone and does not block.
            var trackedPaths = await db.ChapterFiles.Where(f => f.SeriesId == id)
                .Select(f => f.RelativePath).ToListAsync(ct);
            foreach (var path in trackedPaths)
            {
                var key = LibraryPaths.ComparisonKey(path);
                if (LibraryPaths.Resolve(series.RootFolder.Path, key) is { } from && System.IO.File.Exists(from)
                    && LibraryPaths.Resolve(destination.Path, key) is var to
                    && (to is null || !System.IO.File.Exists(to)))
                {
                    return this.Fail(localizer, "error.series.filesNotMoved",
                        new { folder = to ?? Path.Combine(destination.Path, key) });
                }
            }
        }

        var oldRootFolderPath = series.RootFolder.Path;
        var oldRootFolderId = series.RootFolderId;
        series.RootFolderId = destination.Id;
        try
        {
            // Cancellation must not be observed here: every file has already moved, so a
            // cancelled save would leave the DB pointing at the old root while the files sit
            // in the new one. CancellationToken.None keeps this write unconditional.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            series.RootFolderId = oldRootFolderId;
            logger.LogError(ex, "Could not save the series move for {Title} to {Destination}", series.Title, destination.Path);
            RollbackMoves();

            return this.ServerError(localizer, "error.series.moveSaveFailed", new { message = ex.Message });
        }

        // Best-effort only: the move and the DB save both already succeeded, so a stray empty
        // source folder left behind is cosmetic and must not fail the request.
        foreach (var emptied in emptiedFolders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(emptied).Any())
                {
                    Directory.Delete(emptied, recursive: false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove emptied folder {Folder} after moving series {Title}", emptied, series.Title);
            }
        }

        foreach (var folder in folders)
        {
            kavitaScans.QueueScan(Path.Combine(oldRootFolderPath, folder), series.Id);
            kavitaScans.QueueScan(Path.Combine(destination.Path, folder), series.Id);
        }

        var moved = await UserStateForAsync(series.Id, ct);
        return Ok(SeriesDto.FromEntity(
            series, rating: moved.Rating, notificationMode: moved.NotificationMode,
            titleLanguage: await TitleLanguageAsync(ct)) with
        {
            Warnings = [localizer.Get("error.series.folderMoved",
                new { from = oldRootFolderPath, to = destination.Path })]
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
    /// Refused while a download for this series is in flight (it writes into the old folder
    /// halfway through), and when two chapters would end up sharing a file name. A
    /// <c>fingerprint</c> from the preview is refused when the plan has changed since.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(
        int id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        RenameConfirmRequest? request,
        CancellationToken ct)
    {
        var result = await seriesRename.RenameAsync(id, request?.Fingerprint, ct);
        if (result.Error is null)
        {
            return Ok(result);
        }

        return result.Plan is null
            ? NotFound(new { error = result.Error })
            : Conflict(new { code = result.ErrorCode, error = result.Error, warnings = result.Warnings });
    }

    public record RenameConfirmRequest(string? Fingerprint);

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

        try
        {
            CopyDirectory(source, destination);
        }
        catch
        {
            // The source is untouched until the copy completes, so a half-written copy can go.
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            throw;
        }

        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch
        {
            // Some of the source is already gone. The copy is complete, so restore what is missing
            // from it and drop the copy, leaving the folder whole at its old path for the caller's
            // rollback rather than split across two roots.
            RestoreMissing(destination, source);
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private static void RestoreMissing(string copy, string original)
    {
        Directory.CreateDirectory(original);
        foreach (var file in Directory.GetFiles(copy))
        {
            var target = Path.Combine(original, Path.GetFileName(file));
            if (!System.IO.File.Exists(target))
            {
                System.IO.File.Copy(file, target);
            }
        }

        foreach (var dir in Directory.GetDirectories(copy))
        {
            RestoreMissing(dir, Path.Combine(original, Path.GetFileName(dir)));
        }
    }

    private static void RefuseLink(string path)
    {
        if (LibraryPaths.IsLink(path))
        {
            throw new IOException($"Refusing to copy the symbolic link or junction {path}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            RefuseLink(file);
            System.IO.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            RefuseLink(dir);
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    public record MonitorModeRequest(string Mode);

    /// <summary>Rating on a 1–10 scale, or null to clear it.</summary>
    public record SetRatingRequest(int? Rating);

    /// <summary>
    /// Sets the monitor mode, which governs chapters released <em>later</em> and nothing else.
    /// <para>
    /// It deliberately does not touch existing chapters' <see cref="Chapter.Wanted"/> flags. It used
    /// to rewrite every one of them, so switching a long series to Smart silently unticked hundreds
    /// of chapters and wiped whatever the user had chosen by hand.
    /// </para>
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/monitormode")]
    public async Task<IActionResult> SetMonitorMode(int id, [FromBody] MonitorModeRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<NewChapterMonitorMode>(request.Mode, true, out var mode))
        {
            return this.Fail(localizer, "error.series.unknownMonitorMode", new { mode = request.Mode });
        }

        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        series.MonitorNewItems = mode;
        await db.SaveChangesAsync(ct);
        return Ok(new { mode = mode.ToString() });
    }

    /// <param name="UpgradeProfileId">Null clears the pin so the series follows the instance default.</param>
    public record UpgradeProfileRequest(int? UpgradeProfileId);

    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/upgradeprofile")]
    public async Task<IActionResult> SetUpgradeProfile(int id, [FromBody] UpgradeProfileRequest request, CancellationToken ct)
    {
        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        if (request.UpgradeProfileId is { } profileId && !await db.UpgradeProfiles.AnyAsync(p => p.Id == profileId, ct))
        {
            return this.Fail(localizer, "error.upgrades.profileNotFound");
        }

        series.UpgradeProfileId = request.UpgradeProfileId;
        await db.SaveChangesAsync(ct);
        return Ok(new { upgradeProfileId = series.UpgradeProfileId });
    }

    /// <param name="Mode">"Default", "On" or "Off".</param>
    public record ReadFileCleanupRequest(string Mode);

    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{id:int}/readcleanup")]
    public async Task<IActionResult> SetReadFileCleanup(int id, [FromBody] ReadFileCleanupRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<ReadFileCleanup>(request.Mode, true, out var mode) || !Enum.IsDefined(mode))
        {
            return this.Fail(localizer, "error.series.invalidReadCleanup");
        }

        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        series.ReadFileCleanup = mode;
        await db.SaveChangesAsync(ct);
        return Ok(new { readFileCleanup = mode.ToString() });
    }

    /// <param name="UpgradeProfileId">Null clears every pin so the series follow the instance default.</param>
    public record BulkUpgradeProfileRequest(List<int>? SeriesIds, int? UpgradeProfileId);

    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("upgradeprofile/bulk")]
    public async Task<IActionResult> SetUpgradeProfileBulk([FromBody] BulkUpgradeProfileRequest request, CancellationToken ct)
    {
        if (request.UpgradeProfileId is { } profileId && !await db.UpgradeProfiles.AnyAsync(p => p.Id == profileId, ct))
        {
            return this.Fail(localizer, "error.upgrades.profileNotFound");
        }

        // Resolved through db.Series, like the notification bulk: ids outside the caller's root
        // folders are dropped by the query filter instead of written.
        var wanted = (request.SeriesIds ?? []).Distinct().ToList();
        var series = await db.Series.Where(s => wanted.Contains(s.Id)).ToListAsync(ct);
        foreach (var s in series)
        {
            s.UpgradeProfileId = request.UpgradeProfileId;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = series.Count });
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
            return this.Fail(localizer, "error.series.unknownIncognitoMode", new { mode = request.Mode });
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
            return this.Fail(localizer, "error.series.invalidRating");
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

    /// <summary>One of the <see cref="SeriesNotificationMode"/> names.</summary>
    public record SetSeriesNotificationsRequest(string Mode);

    /// <summary>The same mode applied to a whole selection, for the Library's bulk action.</summary>
    public record BulkSeriesNotificationsRequest(List<int>? SeriesIds, string Mode);

    /// <summary>
    /// Sets how loudly <em>this user</em> wants to hear about this series in their inbox.
    /// "Default" defers to their global setting, "All" always notifies, "Reading" only while they
    /// still have unfinished progress on it, "Muted" never.
    /// </summary>
    // No permission beyond being signed in, for the same reason as the rating above: the value
    // lives in the caller's own UserSeriesState row and changes nobody else's notifications.
    [HttpPost("{id:int}/notifications")]
    public async Task<IActionResult> SetNotificationMode(
        int id, [FromBody] SetSeriesNotificationsRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<SeriesNotificationMode>(request.Mode, true, out var mode))
        {
            return this.Fail(localizer, "error.series.unknownNotificationMode", new { mode = request.Mode });
        }

        if (!await db.Series.AnyAsync(s => s.Id == id, ct))
        {
            return NotFound();
        }

        var state = await db.UserSeriesStates.FirstOrDefaultAsync(s => s.SeriesId == id, ct);
        if (state is null)
        {
            state = new UserSeriesState { SeriesId = id };
            db.UserSeriesStates.Add(state);
        }

        state.NotificationMode = mode;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { notificationMode = mode.ToString() });
    }

    /// <summary>
    /// Applies one notification mode across a whole selection in a single call. The Library's bulk
    /// bar can hand this several hundred ids, which is why it is a real endpoint rather than the
    /// client looping the per-series one.
    /// </summary>
    [HttpPost("notifications/bulk")]
    public async Task<IActionResult> SetNotificationModeBulk(
        [FromBody] BulkSeriesNotificationsRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<SeriesNotificationMode>(request.Mode, true, out var mode))
        {
            return this.Fail(localizer, "error.series.unknownNotificationMode", new { mode = request.Mode });
        }

        var wanted = (request.SeriesIds ?? []).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return Ok(new { updated = 0 });
        }

        // Resolved through db.Series rather than trusted from the body: the root-folder query
        // filter drops ids this caller cannot see, so a guessed id writes nothing at all.
        var visible = await db.Series
            .Where(s => wanted.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (visible.Count == 0)
        {
            return Ok(new { updated = 0 });
        }

        var existing = await db.UserSeriesStates
            .Where(s => visible.Contains(s.SeriesId))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var state in existing)
        {
            state.NotificationMode = mode;
            state.UpdatedAt = now;
        }

        // The rest have never had a row — no rating, no reader override — so one is created here
        // carrying only the mode. UserId is stamped by the IUserOwned hook, as on the rating write.
        db.UserSeriesStates.AddRange(visible
            .Except(existing.Select(s => s.SeriesId))
            .Select(seriesId => new UserSeriesState
            {
                SeriesId = seriesId,
                NotificationMode = mode,
                UpdatedAt = now,
            }));

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = visible.Count });
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
            return this.Fail(localizer, "error.series.unknownTagIds");
        }

        series.UserTags.Clear();
        series.UserTags.AddRange(tags);
        await db.SaveChangesAsync(ct);
        return Ok(new { tagIds = series.UserTags.Select(t => t.Id).ToList() });
    }

    /// <summary>The "unmonitor specials" setting turns a requested All into MainOnly.</summary>
}
