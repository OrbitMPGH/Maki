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

        foreach (var series in stale)
        {
            try
            {
                await metadataRefresh.RefreshAsync(series, includeCover: false, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metadata refresh failed for {Title}", series.Title);
            }
        }

        await db.SaveChangesAsync(ct);
        if (stale.Count > 0)
        {
            logger.LogInformation("Refreshed metadata for {Count} series", stale.Count);
        }
    }
}
