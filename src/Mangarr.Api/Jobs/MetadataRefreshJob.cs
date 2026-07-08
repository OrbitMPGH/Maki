using Mangarr.Core.Metadata;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Daily metadata re-sync: status changes (Ongoing → Completed) matter for the
/// ComicInfo Count field, and overview/genres drift over time.
/// </summary>
[DisallowConcurrentExecution]
public class MetadataRefreshJob(
    MangarrDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    ILogger<MetadataRefreshJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var provider = metadataProviders.First();

        var stale = await db.Series
            .Where(s => s.MangaBakaId != null &&
                        (s.LastMetadataRefresh == null || s.LastMetadataRefresh < DateTime.UtcNow.AddHours(-20)))
            .ToListAsync(ct);

        foreach (var series in stale)
        {
            try
            {
                var metadata = await provider.GetAsync(series.MangaBakaId!.Value.ToString(), ct);
                if (metadata is null)
                {
                    continue;
                }

                series.Status = metadata.Status;
                series.Overview = metadata.Description ?? series.Overview;
                series.Genres = [.. metadata.Genres];
                series.Tags = [.. metadata.Tags];
                series.TotalChapters = metadata.TotalChapters ?? series.TotalChapters;
                series.TotalVolumes = metadata.TotalVolumes ?? series.TotalVolumes;
                series.AuthorStory = metadata.AuthorStory ?? series.AuthorStory;
                series.AuthorArt = metadata.AuthorArt ?? series.AuthorArt;
                series.LastMetadataRefresh = DateTime.UtcNow;
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
