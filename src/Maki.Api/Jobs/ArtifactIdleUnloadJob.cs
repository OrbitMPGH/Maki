using System.Globalization;
using Maki.Metadata.Catalogue;
using Maki.Metadata.CoRead;
using Maki.Metadata.ReaderCohorts;
using Maki.Metadata.RecoGraph;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Gives back the memory the discovery artifacts hold once nothing has read them for a while.
///
/// <para>
/// Measured on a 126,865-row catalogue, as live bytes: the catalogue indexes are 33.5 MB, the
/// co-read graph 30.1 MB, the reader cohorts 6.5 MB and the co-recommendation graph 4.7 MB, and
/// releasing all four gave back 74.2 MB of managed heap and 71.9 MB of RSS. So an instance nobody
/// is browsing was holding ~72 MB it had no use for. That matters because this ships to NAS
/// hardware where 8 GB is common and Maki sitting at most of a gigabyte is not something a user can
/// shrug off.
/// </para>
///
/// <para>
/// Quoted as LIVE bytes on purpose. Each artifact's RSS delta as it loads is larger — 52 MB for the
/// catalogue indexes rather than 33.5 — because that figure includes the heap the build churned
/// through on the way, which is not memory the unload can hand back.
/// </para>
///
/// <para>
/// The price is the rebuild on the next read: about nine seconds for the catalogue indexes, a
/// second or two for the graphs. That is why the default window is generous rather than tight - it
/// is meant to catch an instance that is genuinely idle overnight, not to interrupt somebody
/// browsing. <c>MAKI_ARTIFACT_IDLE_MINUTES=0</c> keeps everything loaded forever, which is what a
/// machine with RAM to spare wants.
/// </para>
///
/// <para>
/// The vector index is deliberately NOT unloaded here even though it is the largest single artifact.
/// Every recommendation and every search needs it, so it is the one most likely to be wanted again
/// straight after being dropped, and it is the most expensive thing in the process to rebuild.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class ArtifactIdleUnloadJob(
    CatalogueIndexCache catalogue,
    CoReadCache coRead,
    RecoGraphCache recoGraph,
    ReaderCohortCache cohorts,
    ILogger<ArtifactIdleUnloadJob> logger) : IJob
{
    public static readonly JobKey Key = new("artifact-idle-unload");

    public const string IdleMinutesVariable = "MAKI_ARTIFACT_IDLE_MINUTES";

    private const int DefaultIdleMinutes = 30;

    public Task Execute(IJobExecutionContext context)
    {
        var minutes = Resolve(Environment.GetEnvironmentVariable(IdleMinutesVariable));
        if (minutes <= 0)
        {
            return Task.CompletedTask;
        }

        var idleFor = TimeSpan.FromMinutes(minutes);
        var released = 0;
        released += catalogue.ReleaseIfIdle(idleFor) ? 1 : 0;
        released += coRead.ReleaseIfIdle(idleFor) ? 1 : 0;
        released += recoGraph.ReleaseIfIdle(idleFor) ? 1 : 0;
        released += cohorts.ReleaseIfIdle(idleFor) ? 1 : 0;

        if (released > 0)
        {
            // Dropping the references only frees managed heap; without this the process keeps every
            // page and the number an operator actually watches does not move. Measured on a real
            // instance: the catalogue indexes unloaded and RSS sat at 459 MB for a further nine
            // minutes, then drifted UP. Collecting here recovers them.
            //
            // A forced blocking compaction is normally the wrong tool, and it is the right one
            // here for a reason specific to this job: it runs only when something was actually
            // released, which by definition means the instance has been idle for the whole window,
            // so there is no request to stall. The large object heap needs asking separately - it
            // is not compacted by default, and these artifacts are mostly large arrays.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            logger.LogDebug("Released {Count} idle discovery artifact(s)", released);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Minutes of idleness before an artifact goes. Zero disables the unload; anything unparseable
    /// or negative falls back to the default rather than being read as "never", since a typo in an
    /// environment variable should not silently pin a hundred megabytes.
    /// </summary>
    internal static int Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultIdleMinutes;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= 0
                ? minutes
                : DefaultIdleMinutes;
    }
}
