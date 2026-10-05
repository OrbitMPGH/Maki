using Maki.Api.Localization;
using Maki.Core.Localization;
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
    PrebuiltIndexInstaller installer, ArtifactBuildGate gate, IMessageCatalog messages, ILogger<PrebuiltIndexJob> logger) : IJob
{
    public static readonly JobKey Key = new("prebuilt-index");

    /// <summary>Job-data flag set by the manual trigger: run even when the freshness check would skip.</summary>
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        var force = context.MergedJobDataMap.TryGetValue(ForceKey, out var flag) && flag is true;

        try
        {
            // One heavy build at a time across every job here; see ArtifactBuildGate.
            using var build = await gate.EnterAsync(nameof(PrebuiltIndexJob), context.CancellationToken);

            var result = await installer.InstallAsync(force, context.CancellationToken);
            if (result.Installed)
            {
                logger.LogInformation("Prebuilt embedding index: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
            else
            {
                logger.LogDebug("Prebuilt embedding index not installed: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-download; the staged file is discarded and the next run starts over.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prebuilt embedding index check failed");
        }
    }

    /// <summary>The installer answers with a catalogue key; the log is English, so render it in English.</summary>
    private string Outcome(string reason, object? args) => messages.GetFor(SupportedLanguages.Default, reason, args);
}
