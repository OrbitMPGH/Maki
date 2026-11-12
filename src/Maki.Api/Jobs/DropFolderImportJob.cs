using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Drop folder tick: runs every minute and lets <see cref="DropFolderImportService.TickAsync"/> decide
/// whether the configured interval has passed, so an interval change applies without a restart.
/// </summary>
[DisallowConcurrentExecution]
public class DropFolderImportJob(DropFolderImportService dropFolder, ILogger<DropFolderImportJob> logger) : IJob
{
    public static readonly JobKey Key = new("drop-folder-import");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await dropFolder.TickAsync(force: false, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Drop folder pass failed");
            context.ReportFailure(ex);
        }
    }
}
