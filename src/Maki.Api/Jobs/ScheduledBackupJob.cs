using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Hourly check behind the opt-in <c>backup.scheduled</c> setting: takes a "scheduled" backup when
/// the newest backup of any kind is older than the health freshness window. The window is shortened
/// by a couple of hours so the next tick lands before the health check would start warning.
/// </summary>
[DisallowConcurrentExecution]
public class ScheduledBackupJob(
    BackupService backups,
    IAppSettings settings,
    AppPaths paths,
    TimeProvider time,
    ILogger<ScheduledBackupJob> logger) : IJob
{
    public static readonly JobKey Key = new("scheduled-backup");
    public const string Kind = "scheduled";

    private static readonly TimeSpan Margin = TimeSpan.FromHours(2);

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        try
        {
            await RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled backup failed");
            context.ReportFailure(ex);
        }
    }

    /// <returns>True when a backup was taken.</returns>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.BackupScheduled, ct) != "true")
            return false;

        var options = JsonSerializer.Deserialize<HealthOptions>(
            await settings.GetAsync(SettingKeys.HealthOptions, ct) ?? "{}", HealthScanService.Json) ?? new();
        var window = TimeSpan.FromDays(Math.Max(options.BackupDays, 1)) - Margin;
        var newest = BackupService.NewestBackupUtc(paths);
        if (newest != DateTime.MinValue && time.GetUtcNow().UtcDateTime - newest < window)
            return false;

        await backups.CreateAsync(Kind, ct);
        return true;
    }
}
