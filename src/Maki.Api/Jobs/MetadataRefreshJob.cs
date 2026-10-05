using Maki.Api.Services;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Daily metadata re-sync: status changes (Ongoing → Completed) matter for the
/// ComicInfo Count field, and overview/genres drift over time.
/// <para>
/// Each series is loaded, refreshed and saved in its own scope, the same shape as
/// <see cref="RefreshMonitoredSeriesJob.RefreshSeriesAsync"/>. On the rate-limited API path a pass
/// runs for a long time, so a restart part way through keeps what it finished, and a row edited or
/// deleted meanwhile costs that one series rather than the whole pass.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class MetadataRefreshJob(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataRefreshJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        List<int> staleIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var cutoff = DateTime.UtcNow.AddHours(-20);
            staleIds = await db.Series
                .Where(s => s.MangaBakaId != null &&
                            (s.LastMetadataRefresh == null || s.LastMetadataRefresh < cutoff))
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        var done = 0;
        foreach (var seriesId in staleIds)
        {
            if (ct.IsCancellationRequested)
            {
                logger.LogInformation("Metadata refresh cancelled after {Done} of {Total} series", done, staleIds.Count);
                return;
            }

            try
            {
                if (await RefreshSeriesAsync(seriesId, ct))
                {
                    done++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a bad series. Without this the catch below reads it as a failure
                // and carries straight on to the next one, so cancelling never ends the pass.
                logger.LogInformation("Metadata refresh cancelled after {Done} of {Total} series", done, staleIds.Count);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogDebug("Series {SeriesId} changed or was deleted during metadata refresh; skipped", seriesId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metadata refresh failed for series {SeriesId}", seriesId);
            }
        }

        if (done > 0)
        {
            logger.LogInformation("Refreshed metadata for {Count} series", done);
        }
    }

    private async Task<bool> RefreshSeriesAsync(int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var metadataRefresh = scope.ServiceProvider.GetRequiredService<SeriesMetadataRefreshService>();

        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null || !await metadataRefresh.RefreshAsync(series, includeCover: false, ct))
        {
            return false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}
