using Mangarr.Metadata.Embedding;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Precomputes description embeddings for the MangaBaka dump so Discover can recommend by
/// "feel". The first run over the full dump takes minutes on CPU; later runs only re-embed
/// series whose text or the model changed, so they finish quickly. Runs a bit after startup
/// and daily; stable key so it can be triggered on demand.
/// </summary>
[DisallowConcurrentExecution]
public class EmbeddingIndexJob(
    SeriesEmbeddingIndexer indexer,
    ILogger<EmbeddingIndexJob> logger) : IJob
{
    public static readonly JobKey Key = new("embedding-index");

    public async Task Execute(IJobExecutionContext context)
    {
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
