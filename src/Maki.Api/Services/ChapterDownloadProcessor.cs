using System.Net;
using Maki.Api.Configuration;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Core.ComicInfo;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Inbox;
using Maki.Core.Naming;
using Maki.Core.Notifications;
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
    ILogger<ChapterDownloadProcessor> logger)
{
    public async Task<DownloadOutcome> ProcessAsync(int queueItemId, CancellationToken ct)
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
                var resolved = await sourceResolver.ResolveAsync(db, chapter, item.SourceMappingId, ct);
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
                await FailAsync(item, "Source returned no pages", ct);
                return DownloadOutcome.Settled;
            }

            item.PagesTotal = pages.Pages.Count;
            await SetStatusAsync(item, QueueStatus.Downloading, ct);

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
            var comicInfo = ComicInfoBuilder.Serialize(ComicInfoBuilder.Build(series, chapter, pageFiles.Count));
            var tmpDir = Path.Combine(rootFolder.Path, ".maki", "tmp");
            var tmpCbz = Path.Combine(tmpDir, $"{item.Id}.cbz");
            CbzPackager.Package(pageFiles, comicInfo, tmpCbz);

            // 6. Atomic move into the library.
            await SetStatusAsync(item, QueueStatus.Importing, ct);
            var relativePath = FileNameBuilder.BuildRelativePath(series, chapter);
            var finalPath = Path.Combine(rootFolder.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(tmpCbz, finalPath, overwrite: true);

            var chapterFile = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = relativePath,
                Size = new FileInfo(finalPath).Length,
                SourceName = mapping.SourceName,
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(chapterFile);
            stats.Record(StatsEventType.ChapterDownloaded, series.Id, series.Title);
            await db.SaveChangesAsync(ct);

            chapter.ChapterFileId = chapterFile.Id;
            item.Status = QueueStatus.Completed;
            item.CompletedAt = DateTime.UtcNow;
            item.NextAttempt = null;
            item.ErrorMessage = null;
            await db.SaveChangesAsync(ct);

            // Downloads from this source are flowing again — reset its escalating rate-limit backoff.
            queue.ClearRateLimitBackoff(mapping.SourceName);

            await BroadcastAsync(item, chapter, series, mapping.SourceName);
            await events.ChapterImported(series.Id, chapter.Id, series.RootFolderId);

            // Part of a batch (series add, search-missing, refresh)? The batch sends one summary
            // when every chapter in it has settled, instead of a ping per chapter.
            if (!batches.Completed(series.Id, item.Id))
            {
                var label = chapter.Number?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                            ?? chapter.Title;

                notifications.Dispatch(NotificationEventType.ChapterDownloaded, new NotificationMessage(
                    NotificationEventType.ChapterDownloaded,
                    Title: "Chapter downloaded",
                    Body: $"{series.Title} — chapter {label}",
                    SeriesTitle: series.Title,
                    SeriesId: series.Id,
                    ChapterNumber: label));

                // Only what nobody asked for. A chapter somebody clicked Download on needs no
                // notification — they watched it happen and the queue already showed them.
                if (item.IsAutomatic)
                {
                    inbox.RaiseForSeries(InboxEventType.ChapterDownloaded, new InboxMessage(
                        Title: "New chapter downloaded",
                        Body: $"{series.Title} — chapter {label} is ready to read",
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
            await CooldownAsync(item, chapter, series, usedMapping?.SourceName ?? "?", retryAfter, ct);
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
            logger.LogError(hre, "Download failed for queue item {Id}. Page not found, retrying.", item.Id);
            var disabledSources = await sourceAvailability.DisabledAsync(ct);
            var mappings = await db.SourceMappings
                .Where(m => m.SeriesId == chapter.SeriesId && m.Enabled && m.Id != item.SourceMappingId &&
                            !disabledSources.Contains(m.SourceName))
                .OrderBy(m => m.Priority)
                .ToListAsync(ct);
            if (mappings.Count == 0)
            {
                await FailAsync(item,
                    $"Page download failed for source {item.SourceMapping?.SourceName}. No more sources to try.", ct);
                return DownloadOutcome.Settled;
            }
            
            // Clear the stale resolution so the recursive call re-verifies the new mapping via
            // ResolveAsync instead of short-circuiting back onto the sourceChapterId that just
            // 404'd (SourceChapterId is null already forces that path regardless of the now-stale
            // SourceMapping navigation, which EF won't refresh just from the FK write below).
            item.SourceMappingId = mappings[0].Id;
            item.SourceChapterId = null;
            item.Status = QueueStatus.Queued;
            item.PagesDone = 0;
            await db.SaveChangesAsync(ct);
            return await ProcessAsync(item.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Download failed for queue item {Id}", item.Id);
            await FailAsync(item, ex.Message, ct);
            return DownloadOutcome.Settled;
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
        item.ErrorMessage = $"Rate limited by {sourceName} — retrying after {until.ToLocalTime():HH:mm:ss}";
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
        item.ErrorMessage = $"Early-access locked on {sourceName} — rechecking after {next:HH:mm}";
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

    private async Task FailAsync(DownloadQueueItem item, string error, CancellationToken ct)
    {
        item.Status = QueueStatus.Failed;
        item.ErrorMessage = error;
        item.RetryCount++;
        item.NextAttempt = queue.NextRetryAttempt(item.RetryCount);
        await db.SaveChangesAsync(ct);
        if (item.Series != null)
        {
            await BroadcastAsync(item, item.Chapter, item.Series, item.SourceMapping?.SourceName ?? "?");
        }

        var chapterLabel = item.Chapter?.Number?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? item.Chapter?.Title;

        // Failures inside a batch are counted into its summary rather than pinged one by one.
        if (batches.Failed(item.SeriesId, item.Id, error))
        {
            return;
        }

        var body = $"{item.Series?.Title ?? "Unknown series"}" +
                   $"{(chapterLabel is null ? "" : $" — chapter {chapterLabel}")}: {error}";

        notifications.Dispatch(NotificationEventType.DownloadFailed, new NotificationMessage(
            NotificationEventType.DownloadFailed,
            Title: "Download failed",
            Body: body,
            Level: NotificationLevel.Error,
            SeriesTitle: item.Series?.Title,
            SeriesId: item.SeriesId,
            ChapterNumber: chapterLabel));

        if (item.IsAutomatic)
        {
            inbox.RaiseForSeries(InboxEventType.DownloadFailed, new InboxMessage(
                Title: "Download failed",
                Body: body,
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
