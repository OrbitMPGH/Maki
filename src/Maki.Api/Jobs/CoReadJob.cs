using Maki.Metadata.CoRead;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Installs the co-read graph published alongside Maki.
///
/// <para>
/// Same shape and same reasoning as <see cref="RecoGraphJob"/>: the artifact cannot be built
/// locally, so a missing one is the normal state of every install and logs at debug rather than
/// warning. Runs last of the artifact downloads, since it is the largest and the least urgent —
/// recommendations work without it.
/// </para>
///
/// <para>
/// Stable key so the settings "Download now" button can trigger it on demand.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class CoReadJob(
    CoReadInstaller installer, ILogger<CoReadJob> logger) : IJob
{
    public static readonly JobKey Key = new("coread-graph");

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
                logger.LogInformation("Co-read graph installed: {Reason}", result.Reason);
            }
            else
            {
                logger.LogDebug("Co-read graph not installed: {Reason}", result.Reason);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-download; the staged file is discarded and the next run starts over.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Co-read graph check failed");
        }
    }
}
