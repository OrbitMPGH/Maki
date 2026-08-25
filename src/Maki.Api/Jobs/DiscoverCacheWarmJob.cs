using Maki.Api.Services;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Pre-warms <see cref="DiscoverService"/>'s rail caches (both the main browse set and the
/// per-genre set) so the first Discover visit after startup or a dump refresh doesn't pay
/// for the full-table scans itself. No-ops quietly when the local MangaBaka database isn't
/// available. Stable key so <see cref="MangaBakaDumpRefreshJob"/> can trigger it right after
/// installing a new dump.
/// </summary>
[DisallowConcurrentExecution]
public class DiscoverCacheWarmJob(
    DiscoverService discover,
    VectorIndexCache searchIndex,
    CatalogueIndexCache catalogueIndex,
    ILogger<DiscoverCacheWarmJob> logger) : IJob
{
    public static readonly JobKey Key = new("discover-cache-warm");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await discover.GetFeedsAsync(refresh: true, context.CancellationToken);
            await discover.GetGenreFeedsAsync(refresh: true, context.CancellationToken);
            // Search's in-memory vector index takes ~8s to build over ~100k series; do it here so
            // the first natural-language query doesn't wear it.
            await searchIndex.GetAsync(context.CancellationToken);
            // Same reasoning for the credit and title-vocabulary indexes: about 9s of scanning the
            // dump, which would otherwise land on whichever keystroke arrived first.
            await catalogueIndex.GetAsync(context.CancellationToken);
        }
        catch (InvalidOperationException)
        {
            // No local MangaBaka database — nothing to warm.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discover cache warm-up failed");
        }
    }
}
