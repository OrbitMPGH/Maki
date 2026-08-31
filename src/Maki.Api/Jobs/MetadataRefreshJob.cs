using Maki.Api.Services;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Daily metadata re-sync: status changes (Ongoing → Completed) matter for the
/// ComicInfo Count field, and overview/genres drift over time.
/// </summary>
[DisallowConcurrentExecution]
public class MetadataRefreshJob(
    MakiDbContext db,
    SeriesMetadataRefreshService metadataRefresh,
    ILogger<MetadataRefreshJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        var stale = await db.Series
            .Where(s => s.MangaBakaId != null &&
                        (s.LastMetadataRefresh == null || s.LastMetadataRefresh < DateTime.UtcNow.AddHours(-20)))
            .ToListAsync(ct);

        var cancelled = false;
        foreach (var series in stale)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            try
            {
                await metadataRefresh.RefreshAsync(series, includeCover: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a bad series. Without this the catch below reads it as a failure
                // and carries straight on to the next one, so cancelling never ends the pass.
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metadata refresh failed for {Title}", series.Title);
            }
        }

        // Whatever this pass did manage to refresh is still worth keeping, so save on the way out
        // of a cancelled run too - on its own token, since ct is exactly what just fired.
        await db.SaveChangesAsync(cancelled ? CancellationToken.None : ct);
        if (cancelled)
        {
            logger.LogInformation("Metadata refresh cancelled by shutdown");
            return;
        }

        if (stale.Count > 0)
        {
            logger.LogInformation("Refreshed metadata for {Count} series", stale.Count);
        }
    }
}
