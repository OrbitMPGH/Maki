using Mangarr.Api.Services;
using Mangarr.Core.Configuration;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Sweeps Failed scraper queue items whose backoff has elapsed and re-queues them, up to a
/// configurable attempt cap — closes the gap where a Failed item only ever retried via a manual
/// click. Torrent items are left alone; <see cref="CompletedDownloadJob"/> tracks those against
/// qBittorrent directly.
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
