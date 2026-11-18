using System.Collections.Concurrent;
using Maki.Api.Jobs;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Services;

/// <summary>
/// The fold check for one series, right after it is matched: counts the pages its split floors wait
/// on, hashes the pages the merges those counts nominate wait on, and folds what the pages confirm,
/// instead of leaving that to the two-hourly job. Smart Download
/// waits for it like it waits for the source scout (<see cref="SmartDownloadJob.WaitsForMeasurementAsync"/>),
/// so the first batch already knows which part rows are part of a whole and which source is best for it.
/// A check never holds a series past <see cref="Deadline"/>: whatever is still open then (a failing
/// source) downloads as it would without folds, and the job picks it up later.
/// </summary>
public sealed class ChapterFoldChecks(IServiceScopeFactory scopes, ILogger<ChapterFoldChecks> logger)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    /// <summary>Chapters counted, and chapters hashed, per round; rounds repeat while they make progress.</summary>
    public const int ChaptersPerRound = 40;

    private readonly ConcurrentDictionary<int, byte> _running = new();

    public bool Running(int seriesId) => _running.ContainsKey(seriesId);

    public void Start(int seriesId)
    {
        if (_running.TryAdd(seriesId, 0))
        {
            _ = Task.Run(() => RunAsync(seriesId));
        }
    }

    private async Task RunAsync(int seriesId)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        try
        {
            await CheckAsync(seriesId, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            logger.LogInformation("The fold check of series {SeriesId} ran out of time; the rest waits for the page hash job", seriesId);
            try
            {
                // What the rounds so far settled still folds now, not at the job's next run.
                using var last = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await FoldAsync(seriesId, last.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fold the checked chapters of series {SeriesId}", seriesId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The fold check of series {SeriesId} failed", seriesId);
        }
        finally
        {
            _running.TryRemove(seriesId, out _);
            await TriggerSmartAsync(seriesId);
        }
    }

    /// <summary>The check itself, awaited; also what tests call.</summary>
    internal async Task CheckAsync(int seriesId, CancellationToken ct)
    {
        // Each round folds what it settled, so a slow source (one browser page per chapter) holds back
        // only the floors it has not reached yet.
        while (true)
        {
            int progress;
            using (var scope = scopes.CreateScope())
            {
                var counts = await scope.ServiceProvider.GetRequiredService<ChapterPageCountService>()
                    .RunAsync(ChaptersPerRound, ct, seriesId);
                var hashes = await scope.ServiceProvider.GetRequiredService<ChapterPageHashService>()
                    .RunAsync(ChaptersPerRound, ct, seriesId);
                progress = counts.Counted + hashes.Hashed;
            }

            await FoldAsync(seriesId, ct);
            if (progress == 0)
            {
                break;
            }
        }
    }

    private async Task FoldAsync(int seriesId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var folded = await scope.ServiceProvider.GetRequiredService<ChapterFoldService>().ApplyAsync(seriesId, ct);
        if (folded > 0)
        {
            logger.LogInformation("Folded {Count} split chapter(s) of series {SeriesId} after matching", folded, seriesId);
        }
    }

    private async Task TriggerSmartAsync(int seriesId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            if (await db.Series.AnyAsync(s => s.Id == seriesId && s.MonitorNewItems == NewChapterMonitorMode.Smart) &&
                scope.ServiceProvider.GetService<ISchedulerFactory>() is { } schedulers)
            {
                await SmartDownloadJob.TriggerAsync(schedulers, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not trigger Smart Download after the fold check of series {SeriesId}", seriesId);
        }
    }
}
