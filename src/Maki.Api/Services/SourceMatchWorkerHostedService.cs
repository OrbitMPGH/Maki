using System.Collections.Concurrent;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Runs auto source matching off the request thread, and links the files a library import
/// registered once their series has chapters to link them to.
/// <para>
/// Adding a series used to await a title search against every registered source plus the first
/// chapter sync, which is tens of seconds of network on a button click. The row, its folder and its
/// cover are all that <c>Add</c> waits for now; this picks the series up afterwards, and the series
/// page shows a spinner where the sources table will be until it finishes. A library import does
/// the same for every folder, so a hundred-folder import is no longer paced by the source sites.
/// </para>
/// <para>
/// <see cref="MatchReaders"/> series match at once. Matching one series already searches several
/// sources in parallel, so more readers would multiply that fan-out at the same sites and invite
/// rate limiting on a big import. Linking is a separate single reader because it is disk work (the
/// ComicInfo rewrite of every archive): a big series rewriting on a NAS must not hold up the match
/// of a series somebody just added.
/// </para>
/// </summary>
public class SourceMatchWorkerHostedService(
    SourceMatchQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<SourceMatchWorkerHostedService> logger) : BackgroundService
{
    internal const int MatchReaders = 2;

    // A series read twice (a re-flag while its match ran) waits for the run in progress rather
    // than matching beside it; the second run then finds nothing left to do or routes the link.
    private readonly ConcurrentDictionary<int, Task> matching = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        var loops = Enumerable.Range(0, MatchReaders)
            .Select(_ => MatchLoopAsync(stoppingToken))
            .Append(LinkLoopAsync(stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task MatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int seriesId;
            try
            {
                seriesId = await queue.ReadMatchAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunExclusiveAsync(seriesId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The flag stays set, so the next start re-queues this series rather than leaving it
                // sourceless with nothing recording that it was owed a match.
                logger.LogError(ex, "Background source matching crashed for series {Id}", seriesId);
            }
        }
    }

    private async Task RunExclusiveAsync(int seriesId, CancellationToken ct)
    {
        while (true)
        {
            var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (matching.TryAdd(seriesId, run.Task))
            {
                try
                {
                    await MatchAsync(seriesId, ct);
                }
                finally
                {
                    matching.TryRemove(seriesId, out _);
                    run.SetResult();
                }

                return;
            }

            if (matching.TryGetValue(seriesId, out var other))
            {
                await other.WaitAsync(ct);
            }
        }
    }

    private async Task LinkLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var seriesId in queue.LinkReader.ReadAllAsync(ct))
            {
                try
                {
                    await LinkAsync(seriesId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Linking imported files crashed for series {Id}", seriesId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Re-queues anything still flagged from a previous run. A match or link that was in flight
    /// when the process stopped never cleared its flag, and the channels do not survive a restart.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

            var pending = await db.Series
                .Where(s => s.SourceMatchPending || s.PendingImportLink != PendingImportLink.None)
                .Select(s => new { s.Id, s.SourceMatchPending, s.PendingImportLink })
                .ToListAsync(ct);

            foreach (var s in pending)
            {
                if (!s.SourceMatchPending)
                {
                    queue.EnqueueLink(s.Id);
                }
                else
                {
                    queue.Enqueue(s.Id, s.PendingImportLink == PendingImportLink.None
                        ? SourceMatchLane.Interactive
                        : SourceMatchLane.Background);
                }
            }

            if (pending.Count > 0)
            {
                logger.LogInformation("Re-queued {Count} series for source matching from a previous run", pending.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not re-queue pending source matches");
        }
    }

    internal async Task MatchAsync(int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return;
        }

        if (!series.SourceMatchPending)
        {
            // Already matched by a duplicate enqueue, or an import whose match finished before a
            // restart and still owes its link.
            if (series.PendingImportLink != PendingImportLink.None)
            {
                queue.EnqueueLink(series.Id);
            }

            return;
        }

        var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();
        var progress = new HubProgress(events, series.Id, series.RootFolderId);

        var mapped = new List<string>();
        var failed = false;
        try
        {
            var matcher = scope.ServiceProvider.GetRequiredService<SourceMatchService>();
            mapped = await matcher.AutoMatchAsync(series, ct, progress);

            if (mapped.Count > 0)
            {
                var sync = scope.ServiceProvider.GetRequiredService<ChapterSyncService>();
                await sync.SyncSeriesAsync(series.Id, ct);
                await scope.ServiceProvider.GetRequiredService<SourceScoutService>()
                    .StartIfEnabledAsync(db, series.Id, ct);
            }
        }
        catch (Exception ex)
        {
            // Whatever happened, the series is done being told it is waiting: leaving the flag set
            // would spin the page's loader forever and re-queue the same failure at every start.
            logger.LogWarning(ex, "Auto source matching failed for {Title}", series.Title);
            failed = true;
        }

        try
        {
            if (failed)
            {
                // Rows the failed save left tracked would be retried by the save below and fail again.
                db.ChangeTracker.Clear();
                series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
                if (series is null)
                {
                    logger.LogInformation("Series {Id} was deleted during source matching", seriesId);
                    return;
                }
            }

            series.SourceMatchPending = false;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The series was deleted while its match was in flight. There is no flag left to clear
            // and nobody to notify about a series that no longer exists.
            logger.LogInformation("Series {Id} was deleted during source matching", seriesId);
            return;
        }

        // Read fresh rather than off the entity: an import can register files into this series
        // while its match runs, and those still need the link stage.
        var linkOwed = await db.Series
            .Where(s => s.Id == seriesId)
            .Select(s => s.PendingImportLink)
            .FirstOrDefaultAsync(ct) != PendingImportLink.None;

        await events.SourceMatchFinished(series.Id, series.RootFolderId, mapped.Count);

        if (mapped.Count == 0 && !failed)
        {
            await NotifyManualMatchNeededAsync(
                scope.ServiceProvider.GetRequiredService<NotificationService>(),
                scope.ServiceProvider.GetRequiredService<IUserLocaleResolver>(),
                scope.ServiceProvider.GetRequiredService<IMessageCatalog>(),
                series, logger, ct);
        }

        if (linkOwed)
        {
            // No inbox row per imported series: the import already raised one for the whole batch,
            // and a hundred rows saying the same thing is a flood, not a notification.
            queue.EnqueueLink(series.Id);
            return;
        }

        // Off by default: the SignalR event above already redraws the Sources card while the user is
        // looking at it. This is for people who add a series and walk away.
        var inbox = scope.ServiceProvider.GetRequiredService<InboxService>();
        inbox.Raise(InboxEventType.SourceMatchFinished, new InboxMessage(
                Key: mapped.Count > 0 ? "inbox.sourceMatch.matched" : "inbox.sourceMatch.none",
                // Source names are product names and are never translated, so joining them here
                // rather than in the message is safe.
                Params: InboxMessage.Args(new { sources = string.Join(", ", mapped) }),
                Level: mapped.Count > 0 ? NotificationLevel.Info : NotificationLevel.Warning,
                SeriesId: series.Id,
                Url: $"/series/{series.Id}"),
            InboxAudience.SeriesTrackers(series.Id, series.RootFolderId));
    }

    /// <summary>
    /// Links the files a library import registered, now that the match has synced chapters, and
    /// rewrites their ComicInfo.xml in the same pass when the import asked for it. Runs whether or
    /// not a source matched: the rewrite is still owed, and a series with no chapters gets the same
    /// result an import with no sources always got.
    /// <para>
    /// Goes through <see cref="CbzLinkService.LinkFilesAsync"/> again rather than a rescan: that is
    /// what the import itself ran before matching moved here, and only it has the lone-file rule,
    /// the hardlink guard on the rewrite and the volume backfill together.
    /// </para>
    /// </summary>
    internal async Task LinkAsync(int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        int linked = 0, total = 0, rootFolderId;
        using (await SeriesLocks.SeriesAsync(seriesId, ct))
        {
            var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
            // Still matching means an import re-flagged it; the match stage routes it back here.
            if (series is null || series.PendingImportLink == PendingImportLink.None || series.SourceMatchPending)
            {
                return;
            }

            rootFolderId = series.RootFolderId;
            var rootPath = series.RootFolder?.Path;
            if (rootPath is null || !Directory.Exists(rootPath))
            {
                // An unmounted share. Not attempted, so the marker stays and the next start retries.
                logger.LogWarning("Not linking the imported files of {Title} yet: root folder {Root} is not reachable",
                    series.Title, rootPath);
                return;
            }

            try
            {
                var cbz = scope.ServiceProvider.GetRequiredService<CbzLinkService>();
                var updateComicInfo = series.PendingImportLink == PendingImportLink.LinkAndComicInfo;
                var paths = await db.ChapterFiles
                    .Where(f => f.SeriesId == seriesId)
                    .Select(f => f.RelativePath)
                    .ToListAsync(ct);
                foreach (var folder in paths.GroupBy(p => LibraryPaths.TopFolder(p) ?? "", LibraryPaths.FolderComparer))
                {
                    var dir = folder.Key.Length == 0 ? null : LibraryPaths.ResolveNoLinks(rootPath, folder.Key);
                    if (dir is null || !Directory.Exists(dir))
                    {
                        continue;
                    }

                    // A file deleted since the import would throw inside the link and take the whole
                    // pass with it. Its row stays for the next rescan to remove, as it always has.
                    var files = folder
                        .Select(p => LibraryPaths.ResolveNoLinks(rootPath, p))
                        .OfType<string>()
                        .Where(File.Exists)
                        .ToList();
                    total += files.Count;
                    var (folderLinked, _) = await cbz.LinkFilesAsync(
                        series, dir, files, "import", updateComicInfo: updateComicInfo, ct: ct);
                    linked += folderLinked;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Something bigger than one bad archive, which the link logs and skips by itself.
                // Retrying at every start would fail the same way, so the marker is cleared.
                logger.LogWarning(ex,
                    "Linking the imported files of {Title} failed. A rescan from the series page links what it can, but does not rewrite ComicInfo for files it already knows",
                    series.Title);
                db.ChangeTracker.Clear();
                series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
                if (series is null)
                {
                    return;
                }
            }

            series.PendingImportLink = PendingImportLink.None;
            await db.SaveChangesAsync(ct);
        }

        var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();
        await events.SeriesFilesLinked(seriesId, rootFolderId, linked, total);
    }

    /// <summary>
    /// Outbound only: no source matched the title, so somebody has to link one by hand from the
    /// series page. Shared with the synchronous match <see cref="SeriesCreationService"/> runs when
    /// approving a request, which never reaches this worker.
    /// </summary>
    internal static async Task NotifyManualMatchNeededAsync(
        NotificationService notifications, IUserLocaleResolver locales, IMessageCatalog catalog,
        Series series, ILogger logger, CancellationToken ct)
    {
        try
        {
            var locale = await locales.DefaultAsync(ct);
            notifications.Dispatch(
                NotificationEventType.ManualMatchNeeded, new NotificationMessage(
                    NotificationEventType.ManualMatchNeeded,
                    Title: catalog.GetFor(locale, "notify.sourceMatch.none.title"),
                    Body: catalog.GetFor(locale, "notify.sourceMatch.none.body", new { series = series.Title }),
                    Level: NotificationLevel.Warning,
                    SeriesTitle: series.Title,
                    SeriesId: series.Id,
                    Url: $"/series/{series.Id}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the manual match notification for {Title}", series.Title);
        }
    }

    /// <summary>
    /// Pushes each source's progress to the hub as the match runs.
    /// <para>
    /// Not <see cref="Progress{T}"/>: that one hands every callback to the thread pool separately,
    /// so a "matched" could overtake its own "searching" and the card would go backwards. Sending
    /// straight from the reporting thread keeps the sends in the order they were made.
    /// </para>
    /// <para>
    /// The send itself is not awaited — a slow client must not pace the searches — and a failed one
    /// is swallowed: this is decoration on top of <c>sourceMatchFinished</c>, which still delivers
    /// the finished table, and a hub push is not worth failing a match over.
    /// </para>
    /// </summary>
    private sealed class HubProgress(EventBroadcaster events, int seriesId, int rootFolderId)
        : IProgress<SourceMatchStep>
    {
        public void Report(SourceMatchStep step) => _ = SendAsync(step);

        private async Task SendAsync(SourceMatchStep step)
        {
            try
            {
                await events.SourceMatchProgress(seriesId, rootFolderId, step.SourceName, step.State.ToString());
            }
            catch
            {
                // Deliberately quiet: see the class summary.
            }
        }
    }
}
