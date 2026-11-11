using Maki.Api.Services;
using Maki.Core.Configuration;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Searches the indexer for volume releases once per local day, an hour after <c>upgrades.scanHour</c>, for
/// at most <c>upgrades.volumeSearchesPerRun</c> series, least recently searched first. Each series is
/// searched at most weekly. Also expires pending proposals past <c>upgrades.proposalExpiryDays</c>.
/// <c>force=true</c> skips the hour and the marker, for the admin's "Search now". With "volume first" on,
/// every tick also searches a few bulk-added series that wait for it (<see cref="VolumeFirstPassAsync"/>).
/// </summary>
[DisallowConcurrentExecution]
public class UpgradeVolumeSearchJob(
    TorrentUpgradeService torrents, IAppSettings settings, TimeProvider time, ILogger<UpgradeVolumeSearchJob> logger) : IJob
{
    public static readonly JobKey Key = new("upgrade-volume-search");
    public const string ForceKey = "force";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    /// <summary>
    /// Bulk-added series searched per tick, every 30 minutes, so an import list reaches the indexer a few
    /// series at a time and most are searched well inside <see cref="VolumeFirstGate.SearchDueHold"/>.
    /// </summary>
    public const int VolumeFirstPerRun = 5;

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var force = context.MergedJobDataMap.TryGetBooleanValue(ForceKey, out var forced) && forced;
        var options = await UpgradeOptions.LoadAsync(settings, ct);
        var local = UpgradeOptions.LocalNow(time);
        var date = UpgradeOptions.MarkerDate(local);
        var daily = force || (options.Enabled && options.VolumeSearch && local.Hour >= Math.Min(options.ScanHour + 1, 23) &&
                              await settings.GetAsync(SettingKeys.UpgradesLastVolumeSearchDate, ct) != date);
        var volumeFirst = await torrents.VolumeFirstOnAsync(ct);
        if (!daily && !volumeFirst)
        {
            return;
        }

        if (!await Gate.WaitAsync(0, ct))
        {
            logger.LogDebug("Volume search skipped; one is already running");
            return;
        }

        try
        {
            if (volumeFirst)
            {
                await VolumeFirstPassAsync(ct);
            }

            if (!daily)
            {
                return;
            }

            await torrents.ExpireAsync(options.ProposalExpiryDays, ct);
            var approved = await torrents.AutoApprovePendingAsync(ct);
            if (approved > 0)
            {
                logger.LogInformation("Approved {Count} pending volume release(s) automatically", approved);
            }

            var run = await torrents.LoadSettingsAsync(ct);
            var searched = 0;
            // Every series would answer the same instance-wide refusal, so there is nothing to walk.
            var candidates = run is { IndexerConfigured: true, Options: { Enabled: true, VolumeSearch: true } }
                ? await torrents.CandidateSeriesAsync(ct)
                : [];
            foreach (var seriesId in candidates)
            {
                if (searched >= options.VolumeSearchesPerRun)
                {
                    break;
                }

                try
                {
                    var result = await torrents.SearchSeriesAsync(seriesId, ct, respectInterval: true, run: run);
                    if (result.Searched)
                    {
                        searched++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Volume search failed for series {SeriesId}", seriesId);
                    searched++;
                }
            }

            logger.LogInformation("Volume search ran for {Count} series", searched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Volume search failed");
            context.ReportFailure(ex);
        }
        finally
        {
            Gate.Release();
            // Shutdown mid-search leaves the day open so the next boot runs it.
            if (daily && !ct.IsCancellationRequested)
            {
                await settings.SetAsync(SettingKeys.UpgradesLastVolumeSearchDate, date, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Volume first for bulk adds: the series an import list or bulk re-match brought in with nothing on
    /// disk, a few per tick and oldest first, whatever the daily schedule says.
    /// </summary>
    private async Task VolumeFirstPassAsync(CancellationToken ct)
    {
        foreach (var seriesId in await torrents.VolumeFirstDueAsync(VolumeFirstPerRun, ct))
        {
            try
            {
                await torrents.SearchPendingVolumeFirstAsync(seriesId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Volume-first search failed for series {SeriesId}", seriesId);
            }
        }
    }

    /// <summary>Queues a forced run without waiting for it. False when it could not be queued.</summary>
    public static async Task<bool> TriggerAsync(ISchedulerFactory schedulerFactory, ILogger logger)
    {
        try
        {
            var scheduler = await schedulerFactory.GetScheduler();
            await scheduler.TriggerJob(Key, new JobDataMap { [ForceKey] = true });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not trigger the volume search");
            return false;
        }
    }
}
