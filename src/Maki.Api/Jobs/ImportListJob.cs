using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Import list tick: runs every minute and lets <see cref="ImportListService.TickAsync"/> decide
/// whether the configured interval has elapsed, so interval changes apply without a restart.
/// </summary>
[DisallowConcurrentExecution]
public class ImportListJob(ImportListService importLists, ILogger<ImportListJob> logger) : IJob
{
    public static readonly JobKey Key = new("import-lists");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await importLists.TickAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Import list pass failed");
        }
    }
}
