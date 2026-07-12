using Mangarr.Core.Configuration;
using Mangarr.Metadata.Embedding;
using Quartz;

namespace Mangarr.Api.Jobs;

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
        var manual = context.MergedJobDataMap.GetBooleanValueFromString(ManualTriggerKey);
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
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-pass; the next run resumes (unchanged rows are skipped).
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedding index pass failed");
        }
    }
}
