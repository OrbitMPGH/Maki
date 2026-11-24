using Maki.Api.Services;
using Maki.Api.Localization;
using Maki.Core.Localization;
using Maki.Metadata.PageShare;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Installs the page-share table published alongside Maki. Same shape as <see cref="EaseJob"/>: the
/// table cannot be built locally, so a missing one is every install's normal state and logs at debug.
/// </summary>
[DisallowConcurrentExecution]
public class PageShareJob(
    PageShareInstaller installer, ArtifactBuildGate gate, IMessageCatalog messages, ILogger<PageShareJob> logger) : IJob
{
    public static readonly JobKey Key = new("page-share");

    /// <summary>Job-data flag set by the manual trigger: run even when the freshness check would skip.</summary>
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        var force = context.MergedJobDataMap.TryGetValue(ForceKey, out var flag) && flag is true;

        try
        {
            using var build = await gate.EnterAsync(nameof(PageShareJob), context.CancellationToken);

            var result = await installer.InstallAsync(force, context.CancellationToken);
            if (result.Installed)
            {
                build.MarkBuilt();
                logger.LogInformation("Page-share table: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
            else
            {
                logger.LogDebug("Page-share table not installed: {Outcome}", Outcome(result.Reason, result.ReasonArgs));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Page-share table check failed");
            context.ReportFailure(ex);
        }
    }

    private string Outcome(string reason, object? args) => messages.GetFor(SupportedLanguages.Default, reason, args);
}
