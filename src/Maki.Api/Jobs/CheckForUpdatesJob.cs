using Maki.Api.Services;
using Maki.Core.Configuration;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>Polls GitHub Releases for a newer tag once a day. Stable key so settings can
/// trigger a check on demand.</summary>
[DisallowConcurrentExecution]
public class CheckForUpdatesJob(
    UpdateCheckService updateCheck,
    IAppSettings settings,
    ILogger<CheckForUpdatesJob> logger) : IJob
{
    public static readonly JobKey Key = new("check-for-updates");

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        if (await settings.GetAsync(SettingKeys.UpdatesCheckForUpdates, ct) == "false")
        {
            return;
        }

        try
        {
            await updateCheck.CheckAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed");
        }
    }
}
