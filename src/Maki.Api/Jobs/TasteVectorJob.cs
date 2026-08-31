using Maki.Metadata.Taste;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Installs the behavioural item vectors published alongside Maki.
///
/// <para>
/// Same shape and same reasoning as <see cref="CoReadJob"/>: the artifact cannot be built locally,
/// so a missing one is the normal state of every install and logs at debug rather than warning.
/// </para>
///
/// <para>
/// Runs LAST of the four staggered artifact downloads, and not because it is the largest (it is not
/// — ~14 MB against the co-read graph's 16 MB compressed). Installing it invalidates the vector
/// index, so the next request pays for a rebuild; doing that while the index is still being built
/// for the first time would throw the work away and start again.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class TasteVectorJob(
    TasteVectorInstaller installer, ILogger<TasteVectorJob> logger) : IJob
{
    public static readonly JobKey Key = new("taste-vectors");

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
                logger.LogInformation("Behavioural vectors installed: {Reason}", result.Reason);
            }
            else
            {
                logger.LogDebug("Behavioural vectors not installed: {Reason}", result.Reason);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-download; the staged file is discarded and the next run starts over.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Behavioural vector check failed");
        }
    }
}
