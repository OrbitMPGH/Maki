using Maki.Metadata.Embedding;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Installs the prebuilt embedding index published alongside Maki, so a fresh install gets
/// working semantic search and recommendations in a download rather than ~an hour of CPU.
/// Runs shortly after startup and daily; no-ops quietly when the artifact is missing,
/// incompatible with this build, or not newer than what's already installed.
///
/// Stable key so the settings "Download now" button can trigger it on demand.
/// </summary>
[DisallowConcurrentExecution]
public class PrebuiltIndexJob(
    PrebuiltIndexInstaller installer, ILogger<PrebuiltIndexJob> logger) : IJob
{
    public static readonly JobKey Key = new("prebuilt-index");

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
                logger.LogInformation("Prebuilt embedding index installed: {Reason}", result.Reason);
            }
            else
            {
                logger.LogDebug("Prebuilt embedding index not installed: {Reason}", result.Reason);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-download; the staged file is discarded and the next run starts over.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prebuilt embedding index check failed");
        }
    }
}
