using Maki.Metadata.RecoGraph;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Installs the co-recommendation graph published alongside Maki.
///
/// <para>
/// Unlike <see cref="PrebuiltIndexJob"/>, whose artifact is a shortcut around work the install
/// could do itself, this one is the only route to the data: building it locally means days of
/// paced requests against AniList and MAL with a tool that does not ship with the app. So a
/// missing artifact is the normal state and logs at debug, not warning.
/// </para>
///
/// <para>
/// Stable key so the settings "Download now" button can trigger it on demand.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class RecoGraphJob(
    RecoGraphInstaller installer, ILogger<RecoGraphJob> logger) : IJob
{
    public static readonly JobKey Key = new("reco-graph");

    /// <summary>Job-data flag set by the manual trigger: run even when the freshness check would skip.</summary>
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        var force = context.MergedJobDataMap.TryGetValue(ForceKey, out var flag) && flag is true;

        try
        {
            var result = await installer.InstallAsync(force, context.CancellationToken);
            if (result.Installed)
            {
                logger.LogInformation("Co-recommendation graph installed: {Reason}", result.Reason);
            }
            else
            {
                logger.LogDebug("Co-recommendation graph not installed: {Reason}", result.Reason);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-download; the staged file is discarded and the next run starts over.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Co-recommendation graph check failed");
        }
    }
}
