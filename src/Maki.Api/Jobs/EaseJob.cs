using Maki.Api.Services;
using Maki.Api.Localization;
using Maki.Core.Localization;
using Maki.Metadata.Ease;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Installs the EASE weight table published alongside Maki. Same shape as <see cref="CoReadJob"/>:
/// the artifact cannot be built locally, so a missing one is every install's normal state and logs
/// at debug. Runs behind <see cref="ArtifactBuildGate"/> like every other artifact download.
/// </summary>
[DisallowConcurrentExecution]
public class EaseJob(
    EaseInstaller installer, ArtifactBuildGate gate, IMessageCatalog messages, ILogger<EaseJob> logger) : IJob
{
    public static readonly JobKey Key = new("ease-table");

    /// <summary>Job-data flag set by the manual trigger: run even when the freshness check would skip.</summary>
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        var force = context.MergedJobDataMap.TryGetValue(ForceKey, out var flag) && flag is true;

        try
        {
            // One heavy build at a time across every job here; see ArtifactBuildGate.
            using var build = await gate.EnterAsync(nameof(EaseJob), context.CancellationToken);

            var result = await installer.InstallAsync(force, context.CancellationToken);
            if (result.Installed)
            {
                build.MarkBuilt();
                logger.LogInformation("Also-finished table: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
            else
            {
                logger.LogDebug("Also-finished table not installed: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-download; the staged file is discarded and the next run starts over.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Also-finished table check failed");
            context.ReportFailure(ex);
        }
    }

    /// <summary>The installer answers with a catalogue key; the log is English, so render it in English.</summary>
    private string Outcome(string reason, object? args) => messages.GetFor(SupportedLanguages.Default, reason, args);
}
