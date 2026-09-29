using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Precomputes description embeddings for the MangaBaka dump so Discover can recommend by
/// "feel". The first run over the full dump takes minutes on CPU; later runs only re-embed
/// series whose text or the model changed, so they finish quickly. Registered durably with no
/// trigger: it runs only when the settings "Build" button fires it by key.
/// </summary>
[DisallowConcurrentExecution]
public class EmbeddingIndexJob(
    SeriesEmbeddingIndexer indexer,
    VectorIndexCache searchIndex,
    IAppSettings settings,
    ILogger<EmbeddingIndexJob> logger) : IJob
{
    public static readonly JobKey Key = new("embedding-index");

    /// <summary>Job-data flag set by the "Build" endpoint. A run without it is skipped.</summary>
    public const string ManualTriggerKey = "manual";

    public async Task Execute(IJobExecutionContext context)
    {
        // The local pass is CPU-heavy for an hour or more and the prebuilt artifact covers most
        // instances, so a stray schedule must not start it unprompted.
        if (!(context.MergedJobDataMap.TryGetBooleanValue(ManualTriggerKey, out var manual) && manual))
        {
            logger.LogDebug("Skipping embedding index pass; it only runs when started from settings.");
            return;
        }

        if (EmbeddingModelProfile.IsOff(
                await settings.GetAsync(SettingKeys.RecommendationsEmbeddingModel, context.CancellationToken)))
        {
            logger.LogDebug("Skipping embedding index pass; embeddings are turned off.");
            return;
        }

        try
        {
            await indexer.RunAsync(ct: context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-pass; the next run resumes (unchanged rows are skipped).
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedding index pass failed");
        }
        finally
        {
            // New vectors may be on disk even when the pass stopped early, so the in-memory
            // search index is stale either way.
            searchIndex.Invalidate();
        }
    }
}
