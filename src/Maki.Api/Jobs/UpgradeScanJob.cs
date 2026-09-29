using Maki.Api.Services;
using Maki.Core.Configuration;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Runs the upgrade scan once per local day, at or after <c>upgrades.scanHour</c>. Fires every 15
/// minutes and checks the <c>upgrades.lastScanDate</c> marker rather than being rescheduled whenever
/// the hour changes, the same shape as the health scan. <c>force=true</c> in the job data skips the
/// global switch, the hour and the marker, for the library-wide "Scan now". <c>seriesId</c> scans that
/// one series instead, for the series page's "Scan for upgrades", and leaves the marker alone.
/// </summary>
[DisallowConcurrentExecution]
public class UpgradeScanJob(
    UpgradeScanService scans, IAppSettings settings, TimeProvider time, ILogger<UpgradeScanJob> logger,
    UpgradeScanTracker? tracker = null) : IJob
{
    public static readonly JobKey Key = new("upgrade-scan");
    public const string ForceKey = "force";
    public const string SeriesKey = "seriesId";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        if (context.MergedJobDataMap.TryGetIntValue(SeriesKey, out var seriesId))
        {
            tracker?.Running(seriesId);
            try
            {
                tracker?.Finished(seriesId, await scans.ScanSeriesAsync(seriesId, ct));
            }
            catch (UpgradeScanBusyException)
            {
                tracker?.Ended(seriesId, "busy");
                logger.LogDebug("Upgrade scan of series {SeriesId} skipped; one is already running", seriesId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tracker?.Ended(seriesId, "failed");
                logger.LogError(ex, "Upgrade scan of series {SeriesId} failed", seriesId);
            }

            return;
        }

        var force = context.MergedJobDataMap.TryGetBooleanValue(ForceKey, out var forced) && forced;
        var options = await UpgradeOptions.LoadAsync(settings, ct);

        // A forced run is an admin pressing "Scan now", so it overrides the switch as well as the
        // schedule. The timer honours both.
        var local = UpgradeOptions.LocalNow(time);
        var date = UpgradeOptions.MarkerDate(local);
        if (!force && (!options.Enabled || local.Hour < options.ScanHour ||
                       await settings.GetAsync(SettingKeys.UpgradesLastScanDate, ct) == date))
        {
            return;
        }

        try
        {
            await scans.ScanAllAsync(ct, ignoreGlobalSwitch: force);
        }
        catch (UpgradeScanBusyException)
        {
            logger.LogDebug("Upgrade scan skipped; one is already running");
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-scan: leave the day open so the next boot runs it.
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upgrade scan failed");
        }

        // Written even when the scan threw, so a scan that crashes is retried tomorrow rather than
        // every 15 minutes. A busy gate returns above and leaves the day open.
        await settings.SetAsync(SettingKeys.UpgradesLastScanDate, date, CancellationToken.None);
    }

    /// <summary>Queues a forced library-wide scan without waiting for it. False when it could not be queued.</summary>
    public static Task<bool> TriggerAsync(ISchedulerFactory schedulerFactory, ILogger logger) =>
        TriggerAsync(schedulerFactory, logger, new JobDataMap { [ForceKey] = true });

    /// <summary>Queues a scan of one series without waiting for it. False when it could not be queued.</summary>
    public static Task<bool> TriggerSeriesAsync(ISchedulerFactory schedulerFactory, ILogger logger, int seriesId) =>
        TriggerAsync(schedulerFactory, logger, new JobDataMap { [SeriesKey] = seriesId });

    private static async Task<bool> TriggerAsync(ISchedulerFactory schedulerFactory, ILogger logger, JobDataMap data)
    {
        try
        {
            var scheduler = await schedulerFactory.GetScheduler();
            await scheduler.TriggerJob(Key, data);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not trigger the upgrade scan");
            return false;
        }
    }
}
