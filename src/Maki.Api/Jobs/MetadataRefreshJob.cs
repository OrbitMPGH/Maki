using Maki.Api.Services;
using Maki.Core.Entities;
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

    internal async Task<bool> RefreshSeriesAsync(int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var metadataRefresh = scope.ServiceProvider.GetRequiredService<SeriesMetadataRefreshService>();

        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null || await metadataRefresh.LookupAsync(series, ct) is not { } metadata)
        {
            return false;
        }

        // The lookup is the slow part. A hand edit and its lock saved while it ran must be seen
        // before applying, or the refresh would write over a field it loaded as unlocked.
        await db.Entry(series).ReloadAsync(ct);
        if (db.Entry(series).State == EntityState.Detached)
        {
            return false;
        }

        await metadataRefresh.ApplyAsync(series, metadata, includeCover: false, SeriesMetadataField.None, ct);

        await db.SaveChangesAsync(ct);
        await metadataRefresh.PublishChangesAsync(ct);
        return true;
    }
}
