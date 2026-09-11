using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Periodically refreshes chapter lists and queues downloads for new monitored chapters. Series
/// are shuffled so one slow or broken source doesn't always starve the same tail of the library.
/// <para>
/// Only a series that is <see cref="SeriesStatus.Completed"/> <em>and</em> already has every
/// chapter MangaBaka knows about is skipped — there is nothing left for it to discover. Anything
/// still running (or of unknown status, or behind MangaBaka's count) is refreshed.
/// </para>
/// <para>
/// Note the asymmetry: "behind MangaBaka" alone is not enough to decide. MangaBaka's total lags
/// the sources on active titles — several series here are already <em>ahead</em> of it (e.g. 195
/// chapters against a reported 187) — so gating purely on that count would stall exactly the
/// ongoing series that need refreshing, until MangaBaka caught back up.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class RefreshMonitoredSeriesJob(
    IServiceScopeFactory scopeFactory,
    DownloadQueueService queue,
    NotificationService notifications,
    InboxService inbox,
    DownloadBatchNotifier batches,
    SourceAvailability sourceAvailability,
    ILogger<RefreshMonitoredSeriesJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        List<int> seriesIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            seriesIds = await RefreshableSeriesIdsAsync(db, await sourceAvailability.DisabledAsync(ct), ct);
        }

        var done = 0;
        foreach (var seriesId in seriesIds.OrderBy(_ => Random.Shared.Next()))
        {
            if (ct.IsCancellationRequested)
            {
                logger.LogInformation("Refresh cancelled after {Done} of {Total} series", done, seriesIds.Count);
                return;
            }

            try
            {
                await RefreshSeriesAsync(seriesId, ct);
                done++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a broken series. The catch below would read it as a per-series
                // failure and move straight on to the next one, which is why a Ctrl+C mid-refresh
                // used to keep walking the entire library instead of ending the pass.
                logger.LogInformation("Refresh cancelled after {Done} of {Total} series", done, seriesIds.Count);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh failed for series {SeriesId}", seriesId);
            }
        }
    }

    /// <summary>
    /// One series, in its own DI scope. The scope is per-series and not per-job on purpose: a single
    /// <see cref="MakiDbContext"/> held across the whole pass accumulates every chapter row it loads
    /// in the change tracker, and each per-series <c>SaveChangesAsync</c> then runs DetectChanges over
    /// all of them. Cost per series grows with how many came before it, so a large library turned the
    /// refresh quadratic - burning CPU and holding SQLite's writer long enough to stall live requests.
    /// </summary>
    internal async Task RefreshSeriesAsync(int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var chapterSync = scope.ServiceProvider.GetRequiredService<ChapterSyncService>();

        var newChapterIds = await chapterSync.SyncSeriesAsync(seriesId, ct);
        var wanted = await db.Chapters
            .Where(c => newChapterIds.Contains(c.Id) && c.Wanted && c.ChapterFileId == null)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (wanted.Count == 0)
        {
            return;
        }

        var series = await db.Series.Where(s => s.Id == seriesId)
            .Select(s => new { s.Title, s.RootFolderId, s.MonitorNewItems }).FirstOrDefaultAsync(ct);

        // Smart drip-feeds against reading progress, so it must own its own queueing — bulk-grabbing
        // everything the moment it is listed is the exact behaviour it exists to avoid. New chapters
        // are still synced and still count toward the series total; SmartDownloadJob picks them up
        // when the reader gets close. This gate used to be implicit: Chapter.MonitoredUnder returned
        // false for Smart, so the predicate above simply never matched. It matches now.
        var smart = series?.MonitorNewItems == NewChapterMonitorMode.Smart;

        var queuedItemIds = new List<int>();
        if (!smart)
        {
            foreach (var chapterId in wanted)
            {
                if (await queue.EnqueueChapterAsync(
                        chapterId, ct, DownloadOrigin.MonitorRefresh) is { } item)
                {
                    queuedItemIds.Add(item.Id);
                }
            }
        }

        logger.LogInformation(
            smart ? "Series {SeriesId}: found {Count} new chapter(s), left to Smart Download"
                  : "Series {SeriesId}: queued {Count} new chapter(s)",
            seriesId, wanted.Count);

        var title = series?.Title ?? "Unknown series";
        var body = smart
            ? $"{wanted.Count} new chapter(s) available"
            : $"{wanted.Count} new chapter(s) queued for download";

        notifications.Dispatch(NotificationEventType.NewChapterAvailable, new NotificationMessage(
            NotificationEventType.NewChapterAvailable,
            Title: "New chapters available",
            Body: $"{title}: {body}",
            SeriesTitle: title,
            SeriesId: seriesId));

        if (series is not null)
        {
            inbox.Raise(InboxEventType.NewChapterAvailable, new InboxMessage(
                    Title: "New chapters available",
                    Body: $"{title} — {body}",
                    SeriesId: seriesId,
                    Url: $"/series/{seriesId}"),
                InboxAudience.SeriesTrackers(seriesId, series.RootFolderId));
        }

        if (queuedItemIds.Count > 0)
        {
            // The message above already announced the count, so the batch only owes a
            // summary once every one of those chapters has finished (or failed).
            batches.Queued(seriesId, title, queuedItemIds, DownloadOrigin.MonitorRefresh, announce: false);
        }
    }

    /// <summary>
    /// Ids of series worth refreshing: any with an enabled source mapping that is either not
    /// Completed, has no known chapter total, or does not yet hold a chapter whose number reaches
    /// that total. Compared against the highest chapter number we hold, not the count — sources
    /// list specials and one-shots MangaBaka doesn't count, so a count comparison reads as "ahead"
    /// (244 vs 240) on a series that is actually exactly in step.
    /// <paramref name="disabledSources"/> are the globally switched-off sources; their mappings
    /// don't count as enabled, so a series left with none is not refreshed.
    /// </summary>
    internal static Task<List<int>> RefreshableSeriesIdsAsync(
        MakiDbContext db, List<string> disabledSources, CancellationToken ct = default) =>
        db.Series
            .Where(s => s.SourceMappings.Any(m => m.Enabled && !disabledSources.Contains(m.SourceName)))
            .Where(s => s.Status != SeriesStatus.Completed
                        || s.TotalChapters == null
                        || !db.Chapters.Where(c => c.SeriesId == s.Id).Any(c => c.Number >= s.TotalChapters))
            .Select(s => s.Id)
            .ToListAsync(ct);
}
