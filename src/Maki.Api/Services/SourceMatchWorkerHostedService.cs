using Maki.Api.Hubs;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Runs auto source matching off the request thread.
/// <para>
/// Adding a series used to await a title search against every registered source plus the first
/// chapter sync, which is tens of seconds of network on a button click. The row, its folder and its
/// cover are all that <c>Add</c> waits for now; this picks the series up afterwards, and the series
/// page shows a spinner where the sources table will be until it finishes.
/// </para>
/// <para>
/// Deliberately single-reader: matching one series already searches several sources at once, so a
/// second reader would multiply that fan-out at the same sites for no wall-clock win on the series
/// somebody is actually looking at.
/// </para>
/// </summary>
public class SourceMatchWorkerHostedService(
    SourceMatchQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<SourceMatchWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var seriesId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await MatchAsync(seriesId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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

    /// <summary>
    /// Re-queues anything still flagged from a previous run. A match that was in flight when the
    /// process stopped never cleared its flag, and the channel itself does not survive a restart.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

            var pending = await db.Series
                .Where(s => s.SourceMatchPending)
                .Select(s => s.Id)
                .ToListAsync(ct);

            foreach (var id in pending)
            {
                queue.Enqueue(id);
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

    private async Task MatchAsync(int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null || !series.SourceMatchPending)
        {
            // Deleted, or already matched by a duplicate enqueue.
            return;
        }

        var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();
        var progress = new HubProgress(events, series.Id, series.RootFolderId);

        var mapped = new List<string>();
        try
        {
            var matcher = scope.ServiceProvider.GetRequiredService<SourceMatchService>();
            mapped = await matcher.AutoMatchAsync(series, ct, progress);

            if (mapped.Count > 0)
            {
                var sync = scope.ServiceProvider.GetRequiredService<ChapterSyncService>();
                await sync.SyncSeriesAsync(series.Id, ct);
            }
        }
        catch (Exception ex)
        {
            // Whatever happened, the series is done being told it is waiting: leaving the flag set
            // would spin the page's loader forever and re-queue the same failure at every start.
            logger.LogWarning(ex, "Auto source matching failed for {Title}", series.Title);
        }

        series.SourceMatchPending = false;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The series was deleted while its match was in flight. There is no flag left to clear
            // and nobody to notify about a series that no longer exists.
            logger.LogInformation("Series {Id} was deleted during source matching", seriesId);
            return;
        }

        await events.SourceMatchFinished(series.Id, series.RootFolderId, mapped.Count);

        // Off by default: the SignalR event above already redraws the Sources card while the user is
        // looking at it. This is for people who add a series and walk away.
        var inbox = scope.ServiceProvider.GetRequiredService<InboxService>();
        inbox.Raise(InboxEventType.SourceMatchFinished, new InboxMessage(
                Title: mapped.Count > 0 ? "Sources matched" : "No sources matched",
                Body: mapped.Count > 0
                    ? $"{series.Title} — matched {string.Join(", ", mapped)}"
                    : $"{series.Title} — no source had a match, link one by hand to download",
                Level: mapped.Count > 0 ? NotificationLevel.Info : NotificationLevel.Warning,
                SeriesId: series.Id,
                Url: $"/series/{series.Id}"),
            InboxAudience.SeriesTrackers(series.Id, series.RootFolderId));
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
