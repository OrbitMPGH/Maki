using Maki.Api.Services;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Re-refreshes every series whose mapping on one source is currently failing. Manual only: there
/// is no trigger for it, the source outage check on the Health workspace fires it by key once the
/// admin believes the site is back.
/// <para>
/// A job rather than a fire-and-forget task off the request because a source outage can cover
/// hundreds of series, which is minutes of scraping, and Quartz's shutdown path is what lets a
/// restart end the pass cleanly mid-run.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class SourceRetryJob(
    IServiceScopeFactory scopes,
    SourceRetryStatus status,
    RefreshMonitoredSeriesJob refresher,
    ILogger<SourceRetryJob> logger) : IJob
{
    public static readonly JobKey Key = new("source-retry");

    /// <summary>Job-data key: the source whose failing series to retry.</summary>
    public const string SourceKey = "source";

    /// <summary>
    /// Consecutive failures, with nothing recovered yet, that end the pass. The whole point of the
    /// button is that the site came back; if the first handful of series say otherwise then it did
    /// not, and walking the rest only spends several hundred more requests proving the same thing.
    /// </summary>
    private const int GiveUpAfter = 5;

    public async Task Execute(IJobExecutionContext context)
    {
        var sourceName = context.MergedJobDataMap.GetString(SourceKey);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return;
        }

        if (!status.TryBegin(sourceName))
        {
            logger.LogDebug("Source retry skipped; one is already running");
            return;
        }

        var ct = context.CancellationToken;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

            // Filtered in memory rather than in SQL: SQLite compares strings case-sensitively by
            // default and the name arrives from a health check id, which groups names the way the
            // registry writes them rather than the way each mapping stored them.
            var failing = (await db.SourceMappings
                    .Where(m => m.Enabled && m.LastError != null)
                    .Select(m => new { m.Id, m.SeriesId, m.SourceName })
                    .ToListAsync(ct))
                .Where(m => string.Equals(m.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            status.SetTotal(failing.Count);
            logger.LogInformation("Retrying {Count} series failing against {Source}", failing.Count, sourceName);

            var consecutiveFailures = 0;
            var recovered = 0;
            foreach (var mapping in failing)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await refresher.RefreshSeriesAsync(mapping.SeriesId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The sync records per-mapping failures itself; anything thrown out of it is the
                    // series, not the source, so it counts as a failure and the pass moves on.
                    logger.LogWarning(ex, "Retry failed for series {SeriesId}", mapping.SeriesId);
                }

                var cleared = !await db.SourceMappings.AnyAsync(m => m.Id == mapping.Id && m.LastError != null, ct);
                status.ReportSeries(cleared);
                if (cleared)
                {
                    recovered++;
                    consecutiveFailures = 0;
                }
                else if (++consecutiveFailures >= GiveUpAfter && recovered == 0)
                {
                    logger.LogInformation(
                        "Giving up on {Source} after {Count} consecutive failures; the source is still down",
                        sourceName, consecutiveFailures);
                    break;
                }
            }
        }
        finally
        {
            status.End();
        }
    }
}
