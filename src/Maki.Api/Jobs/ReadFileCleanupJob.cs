using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Runs <see cref="ReadFileCleanupService"/> every six hours. The setting is in days, so this only
/// needs to be often enough that a file goes the same day it falls due.
/// </summary>
[DisallowConcurrentExecution]
public class ReadFileCleanupJob(ReadFileCleanupService cleanup, ILogger<ReadFileCleanupJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var deleted = await cleanup.RunAsync(context.CancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation("Read file cleanup deleted {Count} files", deleted);
        }
    }
}
