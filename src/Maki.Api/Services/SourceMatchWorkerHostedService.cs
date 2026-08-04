using Maki.Api.Hubs;
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
/// Deliberately single-reader: matching one series already searches every source in turn, so
/// running two at once just doubles the request rate at the same sites for no wall-clock win on the
/// series somebody is actually looking at.
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

        var mapped = new List<string>();
        try
        {
            var matcher = scope.ServiceProvider.GetRequiredService<SourceMatchService>();
            mapped = await matcher.AutoMatchAsync(series, ct);

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
        await db.SaveChangesAsync(ct);

        var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();
        await events.SourceMatchFinished(series.Id, series.RootFolderId, mapped.Count);
    }
}
