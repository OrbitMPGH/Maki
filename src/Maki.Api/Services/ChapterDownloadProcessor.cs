using System.Globalization;
using System.Net;
using Maki.Api.Configuration;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Core.ComicInfo;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Inbox;
using Maki.Core.Naming;
using Maki.Core.Notifications;
using Maki.Core.Paths;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>What the worker should do with a queue item once processing returns.</summary>
public enum DownloadOutcome
{
    /// <summary>Item reached a terminal state (imported, failed, cancelled) — move on.</summary>
    Settled,

    /// <summary>Source rate-limited us. The item is parked and the caller owns the retry.</summary>
    RateLimited
}

/// <summary>
/// Runs one queue item through the full pipeline:
/// fetch page URLs → download pages → validate → ComicInfo → CBZ → atomic import.
/// </summary>
public class ChapterDownloadProcessor(
    MakiDbContext db,
    SourceRegistry sourceRegistry,
    ChapterSourceResolver sourceResolver,
    PageDownloader pageDownloader,
    EventBroadcaster events,
    AppPaths paths,
    KavitaScanService kavitaScans,
    DownloadQueueService queue,
    InboxService inbox,
    StatsEventService stats,
    NotificationService notifications,
    DownloadBatchNotifier batches,
    SourceAvailability sourceAvailability,
    ReaderArchiveCache archives,
    NamingService naming,
    ChapterFileQualityService quality,
    ILocalizer localizer,
    IUserLocaleResolver locales,
    ILogger<ChapterDownloadProcessor> logger)
{
    /// <summary>A RateLimited row parked for a missing root folder, which no source's cooldown is about.</summary>
    public const string RootFolderUnavailableKey = "error.download.rootFolderUnavailable";

    public Task<DownloadOutcome> ProcessAsync(int queueItemId, CancellationToken ct) =>
        ProcessAsync(queueItemId, [], ct);

    /// <param name="triedMappingIds">
    /// Mappings this item has already 404'd on during this dispatch. The fallback below re-enters
    /// ProcessAsync on the next mapping, and it used to exclude only the mapping in use — so two
    /// mappings that both 404 bounced the item A → B → A → B forever, holding a worker and hammering
    /// both sources with nothing ever settling. Carrying the set across the recursion makes the
    /// fallback strictly narrowing, so it terminates on the mapping count. The set also goes into
    /// <see cref="ChapterSourceResolver.ResolveAsync"/>: excluding it only from the fallback's own
    /// pick is not enough, since the resolver falls back through the priority order whenever the
    /// preferred mapping doesn't list the chapter and would hand back a mapping just ruled out.
    /// </param>
    private async Task<DownloadOutcome> ProcessAsync(int queueItemId, List<int> triedMappingIds, CancellationToken ct)
    {
        var item = await db.DownloadQueue
            .Include(q => q.SourceMapping)
            .Include(q => q.Chapter)
            .Include(q => q.Series)!.ThenInclude(s => s!.RootFolder)
            .FirstOrDefaultAsync(q => q.Id == queueItemId, ct);

        if (item is null || item.Status is QueueStatus.Completed or QueueStatus.Cancelled)
        {
            return DownloadOutcome.Settled;
        }
        if (item.HealthOperationId is { } operationId && !await db.HealthOperations.AnyAsync(o => o.Id == operationId && o.Status == "downloading", ct))
        {
            item.Status = QueueStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            return DownloadOutcome.Settled;
        }

        if (item.Chapter is null)
        {
            // Torrent grabs are handled by CompletedDownloadJob, not the page pipeline.
            return DownloadOutcome.Settled;
        }

        var chapter = item.Chapter;
        var series = item.Series!;
        var rootFolder = series.RootFolder!;

        var workingDir = Path.Combine(paths.DownloadCacheDir, item.Id.ToString());

        // Tracks whichever mapping is actually in use, kept up to date even across the mid-flight
        // fallback in the NotFound catch below, so a rate-limit/failure catch block can attribute
        // the cooldown to the right source instead of guessing from item.SourceMapping (which EF
        // won't have refreshed if the mapping just changed).
        SourceMapping? usedMapping = null;

        try
        {
            // 1. The mapping and source chapter id were already resolved at enqueue time — no
            // network call needed here in the common case. Only re-resolve if the item predates
            // persisted resolution, or its mapping was disabled/removed since it was queued.
            await SetStatusAsync(item, QueueStatus.FetchingPages, ct);

            if (!await RootFolderAvailableAsync(rootFolder, ct))
            {
                await RootFolderUnavailableAsync(item, rootFolder, ct);
                return DownloadOutcome.Settled;
            }

            var disabledSources = await sourceAvailability.DisabledAsync(ct);
            SourceMapping mapping;
            ISource source;
            string sourceChapterId;

            if (item.SourceMapping is { Enabled: true } existingMapping &&
                !disabledSources.Contains(existingMapping.SourceName) &&
                item.SourceChapterId is { } existingChapterId &&
                sourceRegistry.Find(existingMapping.SourceName) is { } existingSource)
            {
                mapping = existingMapping;
                source = existingSource;
                sourceChapterId = existingChapterId;
            }
            else
            {
                if (item.HealthOperationId != null)
                    throw new InvalidOperationException("Approved repair source is no longer available; request a new replacement");
                var resolved = await sourceResolver.ResolveAsync(
                    db, chapter, item.PreferredMappingId ?? item.SourceMappingId, ct, triedMappingIds,
                    onlyPreferred: item.PreferredMappingId != null);
                mapping = resolved.Mapping;
                source = resolved.Source;
                sourceChapterId = resolved.SourceChapterId;

                item.SourceMappingId = mapping.Id;
                item.SourceChapterId = sourceChapterId;
                await db.SaveChangesAsync(ct);
            }

            usedMapping = mapping;

            var sourceChapter = new SourceChapter(
                mapping.SourceName, mapping.SourceSeriesId, sourceChapterId,
                chapter.NumberRaw, chapter.Number, chapter.Volume, chapter.Title,
                chapter.Language, chapter.ReleaseDate);
            var pages = await source.GetPagesAsync(sourceChapter, ct);

            if (pages.Pages.Count == 0)
            {
                await FailAsync(item, "error.download.noPages", ct);
                return DownloadOutcome.Settled;
            }

            item.PagesTotal = pages.Pages.Count;
            await SetStatusAsync(item, QueueStatus.Downloading, ct);

            if (PageCacheManifest.Prepare(workingDir, PageCacheManifest.Key(mapping.Id, sourceChapterId, pages.Pages.Count)))
            {
                logger.LogInformation("Discarded cached pages of queue item {Id}: they came from another source chapter",
                    item.Id);
            }

            // 2. Download pages (resumable — existing files are kept).
            var lastBroadcast = DateTime.MinValue;
            var pageFiles = await pageDownloader.DownloadAsync(pages, mapping.SourceName, workingDir, async (done, _) =>
            {
                item.PagesDone = done;
                if (DateTime.UtcNow - lastBroadcast > TimeSpan.FromSeconds(1))
                {
                    lastBroadcast = DateTime.UtcNow;
                    await BroadcastAsync(item, chapter, series, mapping.SourceName);
                }
            }, ct);

            item.PagesDone = pages.Pages.Count;

            // 3. Validate images.
            await SetStatusAsync(item, QueueStatus.Validating, ct);
            // Undecodable means undecodable, for every source: a page that is not an image is a
            // failed download, never something to package. Sources that pad chapters with tiny
            // separator images (TopManhua does) are handled where the problem actually was — see
            // ImageValidator.MinTrustedLength — rather than by tolerating some number of broken
            // pages here, which shipped corrupt CBZs whenever the count happened to land under it.
            foreach (var file in pageFiles)
            {
                if (!await ImageValidator.IsValidImageAsync(file, ct))
                {
                    File.Delete(file); // force re-download on retry
                    throw new InvalidOperationException($"Invalid image: {Path.GetFileName(file)}");
                }
            }

            // 4–5. ComicInfo + CBZ into a temp dir on the same volume as the library.
            await SetStatusAsync(item, QueueStatus.Packaging, ct);
            if (!await RootFolderAvailableAsync(rootFolder, ct))
            {
                await RootFolderUnavailableAsync(item, rootFolder, ct);
                return DownloadOutcome.Settled;
            }

            var comicInfo = ComicInfoBuilder.Serialize(ComicInfoBuilder.Build(series, chapter, pageFiles.Count));
            var tmpDir = Path.Combine(rootFolder.Path, ".maki", "tmp");
            var tmpCbz = Path.Combine(tmpDir, $"{item.Id}.cbz");
            CbzPackager.Package(pageFiles, comicInfo, tmpCbz);

            if (item.HealthOperationId is { } repairId)
            {
                await HealthOperationService.MutationGate.WaitAsync(ct);
                try
                {
                var operation = await db.HealthOperations.FindAsync([repairId], ct);
                if (operation != null) await db.Entry(operation).ReloadAsync(ct);
                if (operation?.Status != "downloading") throw new InvalidOperationException("Repair is no longer accepting candidates");
                var staged = HealthPaths.Resolve(rootFolder.Path, $".maki/health/{repairId}/chapter-{chapter.Id}.cbz");
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Move(tmpCbz, staged, overwrite: true);
                item.Status = QueueStatus.Completed;
                item.CompletedAt = DateTime.UtcNow;
                item.ClearError();
                await db.SaveChangesAsync(ct);
                TryDeleteDirectory(workingDir);
                return DownloadOutcome.Settled;
                }
                finally { HealthOperationService.MutationGate.Release(); }
            }

            // 6. Atomic move into the library.
            await SetStatusAsync(item, QueueStatus.Importing, ct);
            var desiredPath = await naming.BuildChapterRelativePathAsync(series, chapter, ct);

            // Released right after the save below; the using covers every other way out.
            using var seriesLock = await SeriesLocks.SeriesAsync(series.Id, ct);
            if (item.UpgradeInfoJson is not null)
            {
                return await ApplyUpgradeAsync(item, chapter, series, rootFolder, mapping, source, sourceChapterId,
                    tmpCbz, workingDir, ct);
            }

            var seriesFiles = await db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct);
            var heldByOthers = (await db.Chapters
                    .Where(c => c.SeriesId == series.Id && c.Id != chapter.Id && c.ChapterFileId != null)
                    .Select(c => c.ChapterFileId!.Value)
                    .ToListAsync(ct))
                .ToHashSet();
            var relativePath = await UnclaimedRelativePathAsync(
                rootFolder, series.Id, seriesFiles, heldByOthers, desiredPath, ct);
            var finalPath = Path.Combine(rootFolder.Path, relativePath);
            var chapterFile = seriesFiles.FirstOrDefault(f => SamePath(f.RelativePath, relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(tmpCbz, finalPath, overwrite: true);

            // The archive is in the library now, so nothing from here to the save below is cancellable:
            // a cancel in between left the file on disk with no row, or a row no chapter pointed at.

            // The move above overwrote whatever was at this path, so a re-download (switching a
            // series to a better source, or retrying a bad rip) must update that file's row rather
            // than insert a second one for the same path — the old row would keep pointing at bytes
            // that now belong to the new one, and nothing would ever clean it up.
            var isNewFile = chapterFile is null;

            if (chapterFile is null)
            {
                chapterFile = new ChapterFile
                {
                    SeriesId = series.Id,
                    RelativePath = relativePath,
                    Size = new FileInfo(finalPath).Length,
                    SourceName = mapping.SourceName,
                    DateAdded = DateTime.UtcNow
                };
                db.ChapterFiles.Add(chapterFile);
            }
            else
            {
                chapterFile.RelativePath = relativePath;
                chapterFile.Size = new FileInfo(finalPath).Length;
                chapterFile.SourceName = mapping.SourceName;
                chapterFile.DateAdded = DateTime.UtcNow;

                // New bytes, so nothing about the old rip carries over: Stamp never downgrades a
                // tier or group it finds already set, and a failed measure must leave this unmeasured.
                chapterFile.Tier = QualityTier.Unknown;
                chapterFile.Group = null;
                chapterFile.MeasuredAtUtc = null;

                // Same row id, different archive behind it. The reader caches its page list per
                // ChapterFile, so without this the reader serves the old rip's page names.
                archives.Invalidate(chapterFile.Id);
            }

            chapterFile.SourceChapterId = sourceChapterId;
            var linkGroup = await db.ChapterSourceLinks
                .Where(l => l.ChapterId == chapter.Id && l.SourceMappingId == mapping.Id)
                .Select(l => l.Group)
                .FirstOrDefaultAsync(CancellationToken.None);
            // Not cancellable: the archive is already in the library. Sampled like the backfill, since the
            // pages were validated moments ago and a full second read would only repeat that work.
            quality.Stamp(chapterFile, finalPath, source.Kind, linkGroup ?? ChapterFileQualityService.SiteGroup(source),
                ChapterFileMeasureService.SampleSize, CancellationToken.None);
            await SourceQualitySamples.RecordAsync(db, mapping, chapter.Id, SourceQualityOrigin.Download,
                chapterFile.PageCount, chapterFile.MedianWidth, chapterFile.MedianHeight, chapterFile.Size,
                chapterFile.ImageFormat, DateTime.UtcNow, CancellationToken.None);

            // Only a chapter the library didn't already have counts. A re-download replaces bytes
            // at a path that was already there, so recording it again inflates the instance's
            // download totals and can unlock Library-track achievements nobody earned — switching
            // a 200-chapter series to a better source would post 200 phantom downloads.
            if (isNewFile)
            {
                stats.Record(StatsEventType.ChapterDownloaded, series.Id, series.Title);
            }

            // One save for the file row, the chapter's link and Completed, so none lands without the others.
            chapter.ChapterFile = chapterFile;
            item.Status = QueueStatus.Completed;
            item.CompletedAt = DateTime.UtcNow;
            item.NextAttempt = null;
            item.ClearError();
            await db.SaveChangesAsync(CancellationToken.None);
            seriesLock.Dispose();

            // Downloads from this source are flowing again — reset its escalating rate-limit backoff.
            queue.ClearRateLimitBackoff(mapping.SourceName);

            await BroadcastAsync(item, chapter, series, mapping.SourceName);
            await events.ChapterImported(series.Id, chapter.Id, series.RootFolderId);

            // Part of a batch (series add, search-missing, refresh)? The batch sends one summary
            // when every chapter in it has settled, instead of a ping per chapter.
            if (!await batches.CompletedAsync(series.Id, item.Id))
            {
                var label = chapter.Number?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                            ?? chapter.Title;

                var locale = await locales.DefaultAsync(ct);
                notifications.Dispatch(NotificationEventType.ChapterDownloaded, new NotificationMessage(
                    NotificationEventType.ChapterDownloaded,
                    Title: localizer.GetFor(locale, "notify.chapter.downloaded.title"),
                    Body: localizer.GetFor(locale, "notify.chapter.downloaded.body", new
                    {
                        series = series.Title,
                        hasChapter = label is null ? "no" : "yes",
                        chapter = label ?? string.Empty,
                    }),
                    SeriesTitle: series.Title,
                    SeriesId: series.Id,
                    ChapterNumber: label));

                // Only what nobody asked for. A chapter somebody clicked Download on needs no
                // notification — they watched it happen and the queue already showed them.
                if (item.IsAutomatic)
                {
                    inbox.RaiseForSeries(InboxEventType.ChapterDownloaded, new InboxMessage(
                        Key: "inbox.chapter.downloaded",
                        Params: InboxMessage.Args(new { chapter = label }),
                        SeriesId: series.Id,
                        ChapterId: chapter.Id,
                        Url: $"/series/{series.Id}"), series.Id);
                }
            }

            kavitaScans.QueueScan(Path.Combine(rootFolder.Path, series.FolderName), series.Id);

            TryDeleteDirectory(workingDir);
            logger.LogInformation("Imported {Series} {Chapter} from {Source}",
                series.Title, chapter.Number, mapping.SourceName);
            return DownloadOutcome.Settled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown; startup recovery re-queues in-flight items
        }
        catch (Exception ex) when (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
        {
            // Don't fail the chapter — back this source off and let other trackers keep dispatching.
            var limitedSource = (ex as SourceRateLimitedException)?.SourceName ?? usedMapping?.SourceName ?? "?";
            await CooldownAsync(item, chapter, series, limitedSource, retryAfter, ct);
            return DownloadOutcome.RateLimited;
        }
        catch (ChapterLockedException ex)
        {
            logger.LogInformation("Queue item {Id} still early-access locked: {Message}", item.Id, ex.Message);
            await LockedAsync(item, chapter, series, usedMapping?.SourceName ?? "?", ex.UnlockAt, ct);
            return DownloadOutcome.Settled;
        }
        catch (HttpRequestException hre) when (hre.StatusCode is HttpStatusCode.NotFound)
        {
            if (item.HealthOperationId != null)
            {
                await FailAsync(item, "error.download.approvedSourceNoPages", ct);
                return DownloadOutcome.Settled;
            }
            if (item.PreferredMappingId != null)
            {
                await FailAsync(item, "error.download.pickedSourceUnavailable", ct);
                return DownloadOutcome.Settled;
            }
            logger.LogError(hre, "Download failed for queue item {Id}. Page not found, retrying.", item.Id);

            // The mapping actually in use, which after a previous fallback is not necessarily the one
            // still on the (unrefreshed) navigation property.
            if ((usedMapping?.Id ?? item.SourceMappingId) is { } failedMappingId)
            {
                triedMappingIds.Add(failedMappingId);
            }

            var disabledSources = await sourceAvailability.DisabledAsync(ct);
            var mappings = await sourceResolver.OrderAsync(db, chapter.SeriesId, await db.SourceMappings
                .Where(m => m.SeriesId == chapter.SeriesId && m.Enabled && !triedMappingIds.Contains(m.Id) &&
                            !disabledSources.Contains(m.SourceName))
                .ToListAsync(ct), ct);
            if (mappings.Count == 0)
            {
                await FailAsync(item, "error.download.noMoreSources", ct);
                return DownloadOutcome.Settled;
            }

            // Clear the stale resolution so the recursive call re-verifies the new mapping via
            // ResolveAsync instead of short-circuiting back onto the sourceChapterId that just
            // 404'd (SourceChapterId is null already forces that path regardless of the now-stale
            // SourceMapping navigation, which EF won't refresh just from the FK write below).
            // The row stays in flight: this worker carries on with it, and a Queued row here could be
            // claimed by a second worker onto the same working dir and temp archive.
            item.SourceMappingId = mappings[0].Id;
            item.SourceChapterId = null;
            item.Status = QueueStatus.FetchingPages;
            item.PagesDone = 0;
            await db.SaveChangesAsync(ct);
            TryDeleteDirectory(workingDir);
            return await ProcessAsync(item.Id, triedMappingIds, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Download failed for queue item {Id}", item.Id);
            await FailAsync(item, "error.download.unexpected", ct);
            return DownloadOutcome.Settled;
        }
    }

    /// <summary>
    /// The replacement gate for items carrying an <see cref="UpgradeInfo"/>. The packaged archive is
    /// measured in full and judged again against the file as it is now, under the series' profile as
    /// it is now; a forced item (a user's pick) skips the profile and only faces the hard guards. A
    /// loss deletes the archive and leaves the library untouched. A win moves the old file into
    /// <c>.maki-trash</c> and puts the new one at the same relative path, then updates the same
    /// <see cref="ChapterFile"/> row. Nothing is overwritten in place and no StatsEvent is written.
    /// </summary>
    private async Task<DownloadOutcome> ApplyUpgradeAsync(
        DownloadQueueItem item, Chapter chapter, Series series, RootFolder rootFolder, SourceMapping mapping,
        ISource source, string sourceChapterId, string tmpCbz, string workingDir, CancellationToken ct)
    {
        var info = UpgradeInfo.Parse(item.UpgradeInfoJson);
        var current = info is null || chapter.ChapterFileId != info.ChapterFileId
            ? null
            : await db.ChapterFiles.FirstOrDefaultAsync(f => f.Id == info.ChapterFileId && f.SeriesId == series.Id, ct);
        var finalPath = current is null ? null : LibraryPaths.Resolve(rootFolder.Path, current.RelativePath);
        if (info is null || current is null || finalPath is null || !File.Exists(finalPath))
        {
            TryDeleteFile(tmpCbz);
            await FailAsync(item, "error.download.upgradeTargetGone", ct, permanent: true);
            return DownloadOutcome.Settled;
        }

        // The packaged copy is always a zip; putting it under a .pdf name would corrupt the chapter.
        if (!UpgradeTrash.IsReplaceable(current.RelativePath))
        {
            return await RejectUpgradeAsync(item, chapter, series, mapping, sourceChapterId, tmpCbz, workingDir, info,
                UpgradeReasons.UnsupportedFile, after: null, info.ProfileId, info.ProfileVersion, null, null, null, ct);
        }

        var measurement = ChapterFileMeasurer.MeasureArchive(tmpCbz, 0, ct);
        var size = new FileInfo(tmpCbz).Length;
        await SourceQualitySamples.RecordAsync(db, mapping, chapter.Id, SourceQualityOrigin.Download,
            measurement.PageCount, measurement.MedianWidth, measurement.MedianHeight, size, measurement.ImageFormat,
            DateTime.UtcNow, ct);
        var group = await db.ChapterSourceLinks
            .Where(l => l.ChapterId == chapter.Id && l.SourceMappingId == mapping.Id)
            .Select(l => l.Group)
            .FirstOrDefaultAsync(ct) ?? ChapterFileQualityService.SiteGroup(source);
        var fileName = Path.GetFileName(current.RelativePath);
        var tier = QualityTierResolver.Resolve(source.Kind, null, fileName, isVolume: false);

        var evaluator = await new UpgradeEvaluationService(db, quality).ForSeriesAsync(series.Id, ct);
        QualityScore? before = null;
        QualityScore? candidate = null;
        var reason = UpgradeReasons.UpgradeRejected;
        if (info.Force)
        {
            before = evaluator?.Evaluate(current, chapter.Language)?.Score;
            candidate = evaluator?.Score(evaluator.CandidateFor(mapping.SourceName, group, fileName,
                measurement.PageCount, measurement.MedianWidth, measurement.ImageFormat, size, chapter.Language,
                measurement.MedianHeight));
            tier = candidate?.Tier ?? tier;
            var shared = UpgradeCandidateRules.SharedFile(await db.Chapters.CountAsync(c => c.ChapterFileId == current.Id, ct));
            reason = ForcedGuard(info, current, shared, measurement, evaluator?.Profile.PageTolerancePercent ?? 10);
        }
        else if (evaluator?.Evaluate(current, chapter.Language) is { } evaluated)
        {
            before = evaluated.Score;
            candidate = evaluator.Score(evaluator.CandidateFor(mapping.SourceName, group, fileName,
                measurement.PageCount, measurement.MedianWidth, measurement.ImageFormat, size, chapter.Language,
                measurement.MedianHeight));
            tier = candidate.Tier;
            reason = QualityScorer.IsUpgrade(evaluator.Profile, before, current.PageCount, current.Trusted, candidate,
                measurement.MedianWidth, measurement.PageCount)
                ? null
                : UpgradeReasons.Explain(evaluator.Profile, current.PageCount, candidate, measurement.MedianWidth,
                    measurement.PageCount);
        }

        var after = new QualitySnapshot
        {
            Tier = QualitySnapshot.TierName(tier),
            SourceName = mapping.SourceName,
            SourceChapterId = sourceChapterId,
            Group = group,
            PageCount = measurement.PageCount,
            MedianWidth = measurement.MedianWidth,
            MedianHeight = measurement.MedianHeight,
            ImageFormat = measurement.ImageFormat,
            SizeBytes = size,
            Score = candidate?.Score ?? 0
        };
        var profileId = evaluator?.Profile.Id ?? info.ProfileId;
        var profileVersion = evaluator?.Profile.Version ?? info.ProfileVersion;

        if (reason is not null || (before is null && !info.Force))
        {
            return await RejectUpgradeAsync(item, chapter, series, mapping, sourceChapterId, tmpCbz, workingDir, info,
                reason, after, profileId, profileVersion, measurement.PageCount, measurement.MedianWidth,
                candidate?.Score, ct);
        }

        var beforeSnapshot = UpgradeEvaluator.Snapshot(current, before?.Score ?? 0);
        var trashRelative = UpgradeTrash.NewRelativePath(rootFolder.Path, series.Id, current.Id.ToString(CultureInfo.InvariantCulture), fileName);
        var trashPath = LibraryPaths.Resolve(rootFolder.Path, trashRelative)!;
        UpgradeTrash.EnsureFolder(rootFolder.Path, series.Id);
        var trashBytes = new FileInfo(finalPath).Length;
        if (!await UpgradeTrash.MoveIntoTrashAsync(finalPath, trashPath, logger, ct))
        {
            TryDeleteFile(tmpCbz);
            await FailAsync(item, "error.download.upgradeMoveFailed", ct);
            return DownloadOutcome.Settled;
        }

        try
        {
            File.Move(tmpCbz, finalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not place upgraded {Path}; restoring the original", finalPath);
            try
            {
                File.Move(trashPath, finalPath);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                // The chapter now has no file at its path. Say where the original went, loudly, rather
                // than leave it to be found in the trash (or purged from it).
                logger.LogError(restoreEx, "Could not restore {Trash} to {Path}; the original is stranded in the trash",
                    trashPath, finalPath);
                inbox.Raise(InboxEventType.UpgradeRestoreFailed, new InboxMessage(
                    Key: "inbox.upgrade.restoreFailed",
                    Params: InboxMessage.Args(new
                    {
                        chapter = chapter.Number?.ToString("0.###", CultureInfo.InvariantCulture) ?? chapter.Title,
                        trashPath = trashRelative,
                    }),
                    Level: NotificationLevel.Error,
                    SeriesId: series.Id,
                    ChapterId: chapter.Id,
                    Url: $"/series/{series.Id}"), InboxAudience.Admins);
            }

            TryDeleteFile(tmpCbz);
            await FailAsync(item, "error.download.upgradeMoveFailed", ct);
            return DownloadOutcome.Settled;
        }

        archives.Invalidate(current.Id);

        // The swap is done; from here on nothing may stop the rows describing it from being written.
        var now = DateTime.UtcNow;
        current.Tier = tier;
        current.Group = group;
        current.SourceName = mapping.SourceName;
        current.SourceChapterId = sourceChapterId;
        current.Size = new FileInfo(finalPath).Length;
        current.PageCount = measurement.PageCount;
        current.MedianWidth = measurement.MedianWidth;
        current.MedianHeight = measurement.MedianHeight;
        current.ImageFormat = measurement.ImageFormat;
        current.MeasuredAtUtc = now;
        current.ReplacedAtUtc = now;
        current.ReleaseName = null;
        current.ReleaseHash = null;

        var history = new UpgradeHistory
        {
            SeriesId = series.Id,
            ChapterId = chapter.Id,
            ChapterFileId = current.Id,
            QueueItemId = item.Id,
            ProfileId = profileId,
            ProfileVersion = profileVersion,
            QueuedByUserId = item.QueuedByUserId,
            BeforeJson = beforeSnapshot.Serialize(),
            AfterJson = after.Serialize(),
            TrashPath = trashRelative,
            TrashBytes = trashBytes,
            CreatedAtUtc = now
        };
        db.UpgradeHistory.Add(history);
        await db.SaveChangesAsync(CancellationToken.None);

        info.Outcome = UpgradeOutcomes.Applied;
        info.Reason = null;
        info.After = after;
        info.HistoryId = history.Id;
        item.UpgradeInfoJson = info.Serialize();
        item.Status = QueueStatus.Completed;
        item.CompletedAt = now;
        item.NextAttempt = null;
        item.ClearError();
        await db.SaveChangesAsync(CancellationToken.None);

        queue.ClearRateLimitBackoff(mapping.SourceName);
        await events.QueueUpdated(QueueItemDto.FromEntity(item, chapter, series, mapping.SourceName,
            new UpgradeHistoryState(Reverted: false, TrashAvailable: true)));
        await events.ChapterImported(series.Id, chapter.Id, series.RootFolderId);

        // A user who clicked "Upgrade now" watched it happen; only the unattended scan pings the inbox.
        if (!await batches.CompletedAsync(series.Id, item.Id) && item.IsAutomatic && item.QueuedByUserId is null)
        {
            var label = chapter.Number?.ToString("0.###", CultureInfo.InvariantCulture) ?? chapter.Title;
            inbox.RaiseForSeries(InboxEventType.ChapterUpgraded, new InboxMessage(
                Key: "inbox.upgrade.chapter",
                Params: InboxMessage.Args(new
                {
                    chapter = label,
                    fromSource = beforeSnapshot.SourceName,
                    hasFromWidth = beforeSnapshot.MedianWidth is null ? "no" : "yes",
                    fromWidth = beforeSnapshot.MedianWidth,
                    toSource = after.SourceName,
                    toWidth = after.MedianWidth,
                }),
                SeriesId: series.Id,
                ChapterId: chapter.Id,
                Url: $"/series/{series.Id}"), series.Id);
        }

        kavitaScans.QueueScan(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        TryDeleteDirectory(workingDir);
        logger.LogInformation("Upgraded {Series} {Chapter} from {From} to {To}",
            series.Title, chapter.Number, beforeSnapshot.SourceName, mapping.SourceName);
        return DownloadOutcome.Settled;
    }

    /// <summary>
    /// Why a forced replacement may not go ahead, or null. A shared file would take other chapters'
    /// pages with it, so that guard holds even when the caller may ignore the rest.
    /// </summary>
    private static string? ForcedGuard(UpgradeInfo info, ChapterFile current, bool shared,
        ChapterFileMeasurement measurement, int pageTolerancePercent)
    {
        if (shared) return UpgradeReasons.SharedFile;
        if (info.IgnoreGuards) return null;
        if (current.Trusted) return "trusted";
        if (measurement.MedianWidth is null) return UpgradeReasons.Unmeasurable;
        if (measurement.PageCount < current.PageCount * (1 - pageTolerancePercent / 100.0)) return UpgradeReasons.FewerPages;
        return null;
    }

    /// <summary>
    /// Settles an upgrade that will not be applied: the packaged copy is deleted, the library is left
    /// exactly as it was, the candidate is memoised (unless a user forced it) and the row completes
    /// with its reason recorded.
    /// </summary>
    private async Task<DownloadOutcome> RejectUpgradeAsync(
        DownloadQueueItem item, Chapter chapter, Series series, SourceMapping mapping, string sourceChapterId,
        string tmpCbz, string workingDir, UpgradeInfo info, string? reason, QualitySnapshot? after, int profileId,
        int profileVersion, int? pageCount, int? width, int? score, CancellationToken ct)
    {
        TryDeleteFile(tmpCbz);
        if (!info.Force)
        {
            await UpgradeAttempts.UpsertAsync(db, chapter.Id, series.Id, mapping.Id, sourceChapterId, profileId,
                profileVersion, UpgradeReasons.UpgradeRejected, probed: true, pageCount, width, score, ct);
        }

        info.Outcome = UpgradeOutcomes.Rejected;
        info.Reason = reason ?? UpgradeReasons.UpgradeRejected;
        info.After = after;
        item.UpgradeInfoJson = info.Serialize();
        item.Status = QueueStatus.Completed;
        item.CompletedAt = DateTime.UtcNow;
        item.NextAttempt = null;
        item.ClearError();
        await db.SaveChangesAsync(ct);

        queue.ClearRateLimitBackoff(mapping.SourceName);
        await BroadcastAsync(item, chapter, series, mapping.SourceName);
        await batches.DiscardAsync(series.Id, item.Id);
        TryDeleteDirectory(workingDir);
        logger.LogInformation("Upgrade of {Series} {Chapter} from {Source} rejected: {Reason}",
            series.Title, chapter.Number, mapping.SourceName, info.Reason);
        return DownloadOutcome.Settled;
    }

    /// <summary>
    /// A share that is not mounted is often still a directory (an empty mount point), and creating
    /// folders under it writes the download to the local disk instead. So beyond existing, a root
    /// folder the library already has files in must not be empty. A new, empty root with no files on
    /// record is fine.
    /// </summary>
    private async Task<bool> RootFolderAvailableAsync(RootFolder rootFolder, CancellationToken ct)
    {
        if (!Directory.Exists(rootFolder.Path))
        {
            return false;
        }

        if (Directory.EnumerateFileSystemEntries(rootFolder.Path).Any())
        {
            return true;
        }

        return !await db.ChapterFiles.AnyAsync(
            f => db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == rootFolder.Id), ct);
    }

    /// <summary>
    /// Parks the item until the root folder is back, without counting an attempt or sending a
    /// failure notification: nothing is wrong with the chapter, and every queued item would otherwise
    /// burn its retries and send a failure each while the share is down. RateLimited is claimable
    /// again once NextAttempt passes, so this does not wait on the retry job or its setting.
    /// </summary>
    private async Task RootFolderUnavailableAsync(DownloadQueueItem item, RootFolder rootFolder, CancellationToken ct)
    {
        logger.LogWarning("Root folder {Path} is not available; queue item {Id} will try again later",
            rootFolder.Path, item.Id);
        item.Status = QueueStatus.RateLimited;
        item.SetError(RootFolderUnavailableKey);
        item.NextAttempt = queue.NextRetryAttempt(1);
        await db.SaveChangesAsync(ct);
        if (item.Series != null)
        {
            await BroadcastAsync(item, item.Chapter, item.Series, item.SourceMapping?.SourceName ?? "?");
        }
    }

    /// <summary>
    /// <paramref name="relativePath"/>, or a numbered variant of it when the file there already backs
    /// another chapter. Two one-shots with no distinct title render the same name, and moving over
    /// it would replace the other chapter's pages while both rows point at one file.
    /// </summary>
    private async Task<string> UnclaimedRelativePathAsync(RootFolder rootFolder, int seriesId,
        List<ChapterFile> seriesFiles, HashSet<int> heldByOthers, string relativePath, CancellationToken ct)
    {
        var extension = Path.GetExtension(relativePath);
        var stem = relativePath[..^extension.Length];
        var candidate = relativePath;
        for (var n = 2; n < 1000 && await TakenAsync(candidate); n++)
        {
            candidate = $"{stem} ({n}){extension}";
        }

        return candidate;

        async Task<bool> TakenAsync(string path) =>
            seriesFiles.Any(f => heldByOthers.Contains(f.Id) && SamePath(f.RelativePath, path)) ||
            (File.Exists(Path.Combine(rootFolder.Path, path)) &&
             await HeldByOtherSeriesAsync(rootFolder.Id, seriesId, path, ct));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(LibraryPaths.ComparisonKey(a), LibraryPaths.ComparisonKey(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A series sharing the folder (older data can have one) may own the file already at this path,
    /// and overwriting it would replace that series' archive.
    /// </summary>
    private async Task<bool> HeldByOtherSeriesAsync(int rootFolderId, int seriesId, string relativePath, CancellationToken ct)
    {
        // No length pre-filter: SQLite's length() counts code points and C# counts UTF-16 units, so
        // a name with an emoji would be filtered out and the collision missed.
        var others = await db.ChapterFiles
            .Where(f => f.SeriesId != seriesId &&
                        db.Series.Any(s => s.Id == f.SeriesId && s.RootFolderId == rootFolderId))
            .Select(f => f.RelativePath)
            .ToListAsync(ct);
        return others.Any(p => SamePath(p, relativePath));
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    /// <summary>
    /// Parks the item in <see cref="QueueStatus.RateLimited"/> and starts <paramref name="sourceName"/>'s
    /// cooldown. Only that source backs off — other trackers keep dispatching from the rest of the
    /// queue, and <see cref="DownloadQueueService.ClaimNextAsync"/> picks this item back up once its
    /// tracker's cooldown lifts.
    /// </summary>
    private async Task CooldownAsync(
        DownloadQueueItem item, Chapter chapter, Series series, string sourceName, TimeSpan? retryAfter, CancellationToken ct)
    {
        var until = queue.EnterRateLimitCooldown(sourceName, retryAfter);
        item.Status = QueueStatus.RateLimited;
        item.NextAttempt = until;
        item.SetError("error.download.rateLimited", new { source = sourceName });
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(item, chapter, series, sourceName);

        logger.LogWarning(
            "Rate limited by {Source} on queue item {Id}; backing off until {Until:o}", sourceName, item.Id, until);
    }

    private static readonly TimeSpan LockedRecheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan LockedUnlockBuffer = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Parks a still-early-access-locked chapter in <see cref="QueueStatus.Failed"/> without
    /// incrementing <see cref="DownloadQueueItem.RetryCount"/> or raising a failure notification —
    /// this isn't a broken chapter, it's expected to succeed once the unlock window passes.
    /// Leaving RetryCount untouched keeps it eligible for <see cref="DownloadQueueService.RequeueEligibleFailuresAsync"/>
    /// forever instead of aging out after <c>DownloadRetryMaxAttempts</c>. When the source told us
    /// exactly when early access ends, retry a few minutes after that instead of the blind interval.
    /// </summary>
    private async Task LockedAsync(
        DownloadQueueItem item, Chapter chapter, Series series, string sourceName, DateTimeOffset? unlockAt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var next = unlockAt is { } at ? at.UtcDateTime.Add(LockedUnlockBuffer) : now.Add(LockedRecheckInterval);
        if (next < now)
        {
            // Unlock time already passed (clock skew, or the source's own timer running behind) —
            // don't schedule a retry in the past, just fall back to the normal recheck cadence.
            next = now.Add(LockedRecheckInterval);
        }

        item.Status = QueueStatus.Failed;
        item.NextAttempt = next;
        item.SetError("error.download.earlyAccess", new { source = sourceName });
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(item, chapter, series, sourceName);
    }

    private async Task SetStatusAsync(DownloadQueueItem item, QueueStatus status, CancellationToken ct)
    {
        item.Status = status;
        await db.SaveChangesAsync(ct);
        if (item.Series != null)
        {
            await BroadcastAsync(item, item.Chapter, item.Series, item.SourceMapping?.SourceName ?? "?");
        }
    }

    /// <param name="key">
    /// A catalogue key, not a sentence. It goes to three places that render at different times and
    /// for different readers: the queue row, the batch summary, and the inbox. None of them can be
    /// handed English. These keys take no placeholders, because the batch summary keeps one reason
    /// for a whole series and has nowhere to put values.
    /// </param>
    /// <param name="permanent">
    /// Nothing a retry could change: the row fails without a retry scheduled or counted, and
    /// <see cref="DownloadQueueService.RequeueEligibleFailuresAsync"/> leaves it alone.
    /// </param>
    private async Task FailAsync(DownloadQueueItem item, string key, CancellationToken ct, bool permanent = false)
    {
        item.Status = QueueStatus.Failed;
        item.SetError(key);
        if (permanent)
        {
            item.NextAttempt = null;
        }
        else
        {
            item.RetryCount++;
            item.NextAttempt = queue.NextRetryAttempt(item.RetryCount);
        }

        await db.SaveChangesAsync(ct);
        if (item.Series != null)
        {
            await BroadcastAsync(item, item.Chapter, item.Series, item.SourceMapping?.SourceName ?? "?");
        }

        var chapterLabel = item.Chapter?.Number?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? item.Chapter?.Title;
        if (item.HealthOperationId != null) return;

        // Failures inside a batch are counted into its summary rather than pinged one by one.
        if (await batches.FailedAsync(item.SeriesId, item.Id, key))
        {
            return;
        }

        // Outbound chat and webhooks, not somebody's inbox. There is no reader whose preference
        // could be consulted, so this renders once in the instance's own language. Upgrades never go
        // there: the chapter is already in the library, so a failed replacement is not a lost download.
        if (item.Origin != DownloadOrigin.Upgrade && item.UpgradeInfoJson is null)
        {
            var locale = await locales.DefaultAsync(ct);
            var series = item.Series?.Title ?? localizer.GetFor(locale, "inbox.unknownSeries");
            var body = localizer.GetFor(locale, "notify.download.failed.body", new
            {
                series,
                hasChapter = chapterLabel is null ? "no" : "yes",
                chapter = chapterLabel ?? string.Empty,
                reason = localizer.GetFor(locale, key),
            });

            notifications.Dispatch(NotificationEventType.DownloadFailed, new NotificationMessage(
                NotificationEventType.DownloadFailed,
                Title: localizer.GetFor(locale, "notify.download.failed.title"),
                Body: body,
                Level: NotificationLevel.Error,
                SeriesTitle: item.Series?.Title,
                SeriesId: item.SeriesId,
                ChapterNumber: chapterLabel));
        }

        if (item.IsAutomatic)
        {
            // The chapter label is optional, so it is a parameter the message omits with a
            // `select` rather than a second key. `error` is itself a key, resolved by the renderer
            // in each reader's own language rather than frozen into one here.
            inbox.RaiseForSeries(InboxEventType.DownloadFailed, new InboxMessage(
                Key: "inbox.download.failed",
                Params: InboxMessage.Args(new
                {
                    hasChapter = chapterLabel is null ? "no" : "yes",
                    chapter = chapterLabel,
                    error = key,
                }),
                Level: NotificationLevel.Error,
                SeriesId: item.SeriesId,
                ChapterId: item.ChapterId,
                Url: $"/series/{item.SeriesId}"), item.SeriesId);
        }
    }

    private Task BroadcastAsync(DownloadQueueItem item, Chapter? chapter, Series series, string sourceName) =>
        events.QueueUpdated(QueueItemDto.FromEntity(item, chapter, series, sourceName));

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not clean working dir {Dir}", dir);
        }
    }
}
