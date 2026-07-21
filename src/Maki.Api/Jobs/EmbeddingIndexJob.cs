using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Precomputes description embeddings for the MangaBaka dump so Discover can recommend by
/// "feel". The first run over the full dump takes minutes on CPU; later runs only re-embed
/// series whose text or the model changed, so they finish quickly. Registered with a stable
/// key so it can be triggered on demand; the scheduled (startup + daily) runs only do work
/// when the user has opted into auto-indexing (see <see cref="SettingKeys.RecommendationsAutoIndex"/>).
/// </summary>
[DisallowConcurrentExecution]
public class EmbeddingIndexJob(
    SeriesEmbeddingIndexer indexer,
    VectorIndexCache searchIndex,
    IAppSettings settings,
    ILogger<EmbeddingIndexJob> logger) : IJob
{
    public static readonly JobKey Key = new("embedding-index");

    /// <summary>Job-data flag set by the "Build" endpoint to force a run regardless of the setting.</summary>
    public const string ManualTriggerKey = "manual";

    public async Task Execute(IJobExecutionContext context)
    {
        // A manual "Build" always runs; scheduled runs (startup + daily) are opt-in so a dev
        // restart doesn't kick off the CPU-heavy first pass unprompted.
        // Stored as a real bool by the Build endpoint; absent entirely on scheduled runs.
        var manual = context.MergedJobDataMap.TryGetValue(ManualTriggerKey, out var flag) && flag is true;
        if (!manual)
        {
            var enabled = string.Equals(
                await settings.GetAsync(SettingKeys.RecommendationsAutoIndex, context.CancellationToken),
                "true", StringComparison.OrdinalIgnoreCase);
            if (!enabled)
            {
                logger.LogDebug("Skipping scheduled embedding index pass; auto-indexing is disabled.");
                return;
            }
        }

        try
        {
            await indexer.RunAsync(ct: context.CancellationToken);
            // New vectors on disk; the in-memory search index is now stale.
            searchIndex.Invalidate();
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-pass; the next run resumes (unchanged rows are skipped). Some rows did
            // land, so drop the cached index anyway.
            searchIndex.Invalidate();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedding index pass failed");
        }
    }
}
