using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>Runs <see cref="ReadingStatusAutoService"/> for every reader who switched a rule on.</summary>
[DisallowConcurrentExecution]
public class ReadingStatusAutoJob(
    ReadingStatusAutoService rules,
    ILogger<ReadingStatusAutoJob> logger) : IJob
{
    public static readonly JobKey Key = new("reading-status-rules");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await rules.RunAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading status rules failed");
            context.ReportFailure(ex);
        }
    }
}
