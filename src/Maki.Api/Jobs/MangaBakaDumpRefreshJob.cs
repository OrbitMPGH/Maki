using Maki.Api.Hubs;
using Maki.Metadata.MangaBaka;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Keeps the local MangaBaka database dump current. The dump is published nightly at
/// 00:00 UTC; runs every 6 hours but short-circuits on the published SHA1, so repeat
/// runs cost one tiny checksum request. Also triggerable on demand from settings, and
/// triggered once the setup wizard finishes so a fresh install starts the download then
/// rather than waiting out the schedule.
/// </summary>
[DisallowConcurrentExecution]
public class MangaBakaDumpRefreshJob(
    MangaBakaDumpService dumpService,
    MangaBakaDumpStatus dumpStatus,
    EventBroadcaster events,
    ISchedulerFactory schedulerFactory,
    ArtifactBuildGate gate,
    ILogger<MangaBakaDumpRefreshJob> logger) : IJob
{
    public static readonly JobKey Key = new("mangabaka-dump");

    /// <summary>How often the running refresh's progress is pushed to the UI.</summary>
    private static readonly TimeSpan PushInterval = TimeSpan.FromSeconds(1);

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            // One heavy build at a time across every job here; see ArtifactBuildGate.
            using var build = await gate.EnterAsync(nameof(MangaBakaDumpRefreshJob), context.CancellationToken);

            var installed = await RunWithProgressAsync(context.CancellationToken);

            // A fresh download already has them, built on the staged file. This covers the dump
            // that is already on disk: the browse indexes did not exist before this release, and
            // RefreshAsync short-circuits on an unchanged SHA1, so an existing install would
            // otherwise keep full-scanning until MangaBaka published a new dump. No-ops once built.
            await dumpService.EnsureBrowseIndexesAsync(context.CancellationToken);

            if (installed)
            {
                // Rail caches were built off the old (or no) dump; re-warm against the new one.
                var scheduler = await schedulerFactory.GetScheduler(context.CancellationToken);
                await scheduler.TriggerJob(DiscoverCacheWarmJob.Key, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown. Not a failure: the catch below would log one, and rethrowing would make
            // Quartz log a job error on every restart that happened to land mid-run.
        }
        catch (Exception ex)
        {
            // Health check surfaces prolonged staleness; the next run retries.
            logger.LogWarning(ex, "MangaBaka dump refresh failed");
        }
    }

    /// <summary>
    /// Runs the refresh while pushing its progress to the UI once a second.
    /// <para>
    /// The pump lives here rather than in the service because the service sits in Maki.Metadata,
    /// which knows nothing about SignalR, and because pushing on a timer rather than on every
    /// progress report is what keeps a 350 MB transfer from turning into thousands of messages.
    /// </para>
    /// <para>
    /// The final snapshot is always sent, including on failure: it carries the error text, and
    /// without it a client would be left holding the last in-flight frame forever.
    /// </para>
    /// </summary>
    private async Task<bool> RunWithProgressAsync(CancellationToken ct)
    {
        var refresh = dumpService.RefreshAsync(ct);
        try
        {
            while (!refresh.IsCompleted)
            {
                // Deliberately not cancellable: a cancelled delay completes instantly, which would
                // spin this loop at full speed for as long as the refresh took to unwind.
                await Task.WhenAny(refresh, Task.Delay(PushInterval, CancellationToken.None));
                if (!refresh.IsCompleted)
                {
                    await PushAsync(ct);
                }
            }

            var installed = await refresh;
            await PushAsync(ct);
            return installed;
        }
        catch
        {
            // RefreshAsync has already written the error into the status object.
            await PushAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task PushAsync(CancellationToken ct)
    {
        try
        {
            await events.DumpProgress(dumpStatus.Snapshot());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A hub that cannot deliver must never fail the download.
            logger.LogDebug(ex, "Could not push MangaBaka dump progress");
        }
    }
}
