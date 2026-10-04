using Maki.Api.Services;
using Maki.Core.Configuration;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Searches Prowlarr for volume releases once per local day, an hour after <c>upgrades.scanHour</c>, for
/// at most <c>upgrades.volumeSearchesPerRun</c> series, least recently searched first. Each series is
/// searched at most weekly. Also expires pending proposals past <c>upgrades.proposalExpiryDays</c>.
/// <c>force=true</c> skips the hour and the marker, for the admin's "Search now".
/// </summary>
[DisallowConcurrentExecution]
public class UpgradeVolumeSearchJob(
    TorrentUpgradeService torrents, IAppSettings settings, TimeProvider time, ILogger<UpgradeVolumeSearchJob> logger) : IJob
{
    public static readonly JobKey Key = new("upgrade-volume-search");
    public const string ForceKey = "force";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var force = context.MergedJobDataMap.TryGetBooleanValue(ForceKey, out var forced) && forced;
        var options = await UpgradeOptions.LoadAsync(settings, ct);
        var local = UpgradeOptions.LocalNow(time);
        var date = UpgradeOptions.MarkerDate(local);
        if (!force && (!options.Enabled || !options.VolumeSearch || local.Hour < Math.Min(options.ScanHour + 1, 23) ||
                       await settings.GetAsync(SettingKeys.UpgradesLastVolumeSearchDate, ct) == date))
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
            await torrents.ExpireAsync(options.ProposalExpiryDays, ct);
            var run = await torrents.LoadSettingsAsync(ct);
            var searched = 0;
            // Every series would answer the same instance-wide refusal, so there is nothing to walk.
            var candidates = run is { ProwlarrConfigured: true, Options: { Enabled: true, VolumeSearch: true } }
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
        }
        finally
        {
            Gate.Release();
            // Shutdown mid-search leaves the day open so the next boot runs it.
            if (!ct.IsCancellationRequested)
            {
                await settings.SetAsync(SettingKeys.UpgradesLastVolumeSearchDate, date, CancellationToken.None);
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
