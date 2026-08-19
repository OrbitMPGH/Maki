using Maki.Api.Services;
using Maki.Core.Configuration;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Keeps the scraper queue moving. Two passes:
/// <list type="bullet">
/// <item>re-queue Failed items whose backoff has elapsed, up to a configurable attempt cap — closes
/// the gap where a Failed item only ever retried via a manual click;</item>
/// <item>re-queue rows stuck in an in-flight status with no worker behind them. Startup recovery
/// handles the process having restarted; this handles an owner dying while it keeps running, which
/// otherwise leaves an item reading "Fetching" until somebody restarts the app.</item>
/// </list>
/// Torrent items are left alone in both; <see cref="CompletedDownloadJob"/> tracks those against
/// qBittorrent directly.
/// <para>
/// The orphan sweep deliberately runs before the retry setting is consulted: an unowned row is a
/// broken state to repair, not a retry policy somebody can switch off.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class RetryFailedDownloadsJob(
    DownloadQueueService queue,
    SettingsService settings,
    ILogger<RetryFailedDownloadsJob> logger) : IJob
{
    private const int DefaultMaxAttempts = 5;

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        var orphaned = await queue.SweepOrphanedAsync(ct);
        if (orphaned > 0)
        {
            logger.LogWarning("Re-queued {Count} download(s) left in flight with no worker", orphaned);
        }

        if (await settings.GetAsync(SettingKeys.DownloadRetryEnabled, ct) == "false")
        {
            return;
        }

        var maxAttempts = int.TryParse(await settings.GetAsync(SettingKeys.DownloadRetryMaxAttempts, ct), out var n)
            ? n
            : DefaultMaxAttempts;

        var requeued = await queue.RequeueEligibleFailuresAsync(maxAttempts, ct);
        if (requeued > 0)
        {
            logger.LogInformation("Re-queued {Count} failed download(s) for automatic retry", requeued);
        }
    }
}
