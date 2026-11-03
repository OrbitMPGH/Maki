using Maki.Core.Io;
using Maki.Metadata.MangaBaka;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Adds the browse and franchise tables to an installed dump that predates them, once, shortly
/// after startup. <see cref="MangaBakaDumpRefreshJob"/> backfills too, but its first run waits two
/// minutes and fetches the published checksum first, so an upgrade restarted inside that window
/// kept reading the 6 KB series rows for every rail, vector and credit load. A no-op, costing one
/// schema read, once the tables are there.
/// </summary>
[DisallowConcurrentExecution]
public class MangaBakaTableBackfillJob(
    MangaBakaDumpService dumpService,
    MangaBakaDumpOptions dumpOptions,
    ArtifactBuildGate gate,
    ILogger<MangaBakaTableBackfillJob> logger) : IJob
{
    public static readonly JobKey Key = new("mangabaka-table-backfill");

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        try
        {
            using var build = await gate.EnterAsync(nameof(MangaBakaTableBackfillJob), ct);
            if (await dumpService.EnsureBrowseIndexesAsync(ct))
            {
                build.MarkBuilt();
                PageCache.DropAfterScan(dumpOptions.DatabasePath);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-backfill: the transaction rolls back and the next start tries again.
        }
        catch (Exception ex)
        {
            // The dump refresh backfills as well, so this only delays the speed-up.
            logger.LogWarning(ex, "Backfilling the MangaBaka browse and franchise tables failed");
        }
    }
}
