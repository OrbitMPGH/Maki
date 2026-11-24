using Maki.Api.Services;
using Maki.Core.Io;
using Maki.Core.Sources;
using Maki.Metadata.Catalogue;
using Maki.Metadata.CoRead;
using Maki.Metadata.Ease;
using Maki.Metadata.Embedding;
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
/// The price is the reload on the next read. The window was thirty minutes when that meant nine
/// seconds of rebuilding the catalogue indexes, then ten. It is one minute since the vector index,
/// the catalogue indexes and the co-read graph reload from mapped snapshots: measured on the real
/// catalogue, 0.04 to 0.17 s per artifact, against an instance holding a visit's memory for ten
/// minutes or more after it. <c>MAKI_ARTIFACT_IDLE_MINUTES=0</c> keeps everything loaded forever,
/// which is what a machine with RAM to spare wants.
/// </para>
///
/// <para>
/// The search vectors go too, on their own window (<c>MAKI_VECTOR_IDLE_MINUTES</c>, default 1). They
/// were originally left out on the grounds that every recommendation and every search needs them,
/// and later given a longer window because a rebuild read every vector BLOB. The snapshot reload
/// (about 0.1 s) took that reason away; the separate variable stays so a search-heavy install can
/// keep them longer than the rest.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class ArtifactIdleUnloadJob(
    CatalogueIndexCache catalogue,
    VectorIndexCache vectors,
    CoReadCache coRead,
    EaseCache ease,
    RecoGraphCache recoGraph,
    ReaderCohortCache cohorts,
    SourceChapterListCache chapterLists,
    ILogger<ArtifactIdleUnloadJob> logger,
    RecommendationService? recommendations = null,
    SimilarSeriesService? similarSeries = null) : IJob
{
    public static readonly JobKey Key = new("artifact-idle-unload");

    public const string IdleMinutesVariable = "MAKI_ARTIFACT_IDLE_MINUTES";

    public const string VectorIdleMinutesVariable = "MAKI_VECTOR_IDLE_MINUTES";

    private const int DefaultIdleMinutes = 1;

    private const int DefaultVectorIdleMinutes = 1;

    private static readonly TimeSpan PoolIdleWindow = TimeSpan.FromMinutes(15);

    /// <summary>Above the client's one-hour staleTime, so a Discover feed is never reset under a reader.</summary>
    private static readonly TimeSpan InteractivePoolIdleWindow = TimeSpan.FromHours(2);

    public Task Execute(IJobExecutionContext context)
    {
        // Not an artifact, but the same kind of leftover: chapter lists the scout and measure
        // passes fetched, kept past their five-minute life until the cache filled up.
        chapterLists.DropExpired();

        var minutes = Resolve(Environment.GetEnvironmentVariable(IdleMinutesVariable));
        var vectorMinutes = ResolveVector(Environment.GetEnvironmentVariable(VectorIdleMinutesVariable));
        if (minutes <= 0 && vectorMinutes <= 0)
        {
            return Task.CompletedTask;
        }

        var released = 0;
        if (minutes > 0)
        {
            var idleFor = TimeSpan.FromMinutes(minutes);
            released += catalogue.ReleaseIfIdle(idleFor) ? 1 : 0;
            released += coRead.ReleaseIfIdle(idleFor) ? 1 : 0;
            released += ease.ReleaseIfIdle(idleFor) ? 1 : 0;
            released += recoGraph.ReleaseIfIdle(idleFor) ? 1 : 0;
            released += cohorts.ReleaseIfIdle(idleFor) ? 1 : 0;
            var poolIdle = TimeSpan.FromMinutes(Math.Max(minutes, PoolIdleWindow.TotalMinutes));
            var interactivePoolIdle = TimeSpan.FromMinutes(Math.Max(minutes, InteractivePoolIdleWindow.TotalMinutes));
            released += (recommendations?.ReleaseIdlePools(poolIdle, interactivePoolIdle) ?? 0) > 0 ? 1 : 0;
            released += (similarSeries?.ReleaseIdle(poolIdle) ?? 0) > 0 ? 1 : 0;
        }

        if (vectorMinutes > 0)
        {
            released += vectors.ReleaseIfIdle(TimeSpan.FromMinutes(vectorMinutes)) ? 1 : 0;
        }

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
            // is not compacted by default, and these artifacts are mostly large arrays. Aggressive
            // also decommits the freed regions instead of keeping them for reuse: 127 MB less RSS
            // right after an unload than Forced, measured on WSL2, back to the fresh-start level.
            NativeHeap.CollectAndTrim();
            logger.LogDebug("Released {Count} idle discovery artifact(s)", released);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Minutes of idleness before an artifact goes. Zero disables the unload; anything unparseable
    /// or negative falls back to the default rather than being read as "never", since a typo in an
    /// environment variable should not silently pin a hundred megabytes.
    /// </summary>
    internal static int Resolve(string? value) => IdleWindow.Resolve(value, DefaultIdleMinutes);

    /// <summary>The same for the search vectors, which keep their own variable.</summary>
    internal static int ResolveVector(string? value) => IdleWindow.Resolve(value, DefaultVectorIdleMinutes);
}
