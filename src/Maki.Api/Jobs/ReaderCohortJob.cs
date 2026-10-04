using Maki.Api.Localization;
using Maki.Core.Localization;
using Maki.Metadata.ReaderCohorts;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Installs the reader cohorts published alongside Maki.
///
/// <para>
/// Same shape and same reasoning as <see cref="CoReadJob"/>: the artifact cannot be built locally,
/// so a missing one is the normal state of every install and logs at debug rather than warning.
/// Runs last of the artifact downloads, behind the behavioural vectors — the surfaces that read it
/// are a hint and a rail, which are the least urgent things on the page.
/// </para>
///
/// <para>
/// Stable key so the settings "Download now" button can trigger it on demand.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class ReaderCohortJob(
    ReaderCohortInstaller installer, ArtifactBuildGate gate, IMessageCatalog messages, ILogger<ReaderCohortJob> logger) : IJob
{
    public static readonly JobKey Key = new("reader-cohorts");

    /// <summary>Job-data flag set by the manual trigger: run even when the freshness check would skip.</summary>
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        var force = context.MergedJobDataMap.TryGetValue(ForceKey, out var flag) && flag is true;

        try
        {
            // One heavy build at a time across every job here; see ArtifactBuildGate.
            using var build = await gate.EnterAsync(nameof(ReaderCohortJob), context.CancellationToken);

            var result = await installer.InstallAsync(force, context.CancellationToken);
            if (result.Installed)
            {
                logger.LogInformation("Reader cohorts: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
            else
            {
                logger.LogDebug("Reader cohorts not installed: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-download; the staged file is discarded and the next run starts over.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reader cohort check failed");
        }
    }

    /// <summary>The installer answers with a catalogue key; the log is English, so render it in English.</summary>
    private string Outcome(string reason, object? args) => messages.GetFor(SupportedLanguages.Default, reason, args);
}
