using Maki.Api.Services;
using Quartz;
namespace Maki.Api.Jobs;
[DisallowConcurrentExecution]
public class HealthCheckJob(HealthMonitor monitor) : IJob
{
    public Task Execute(IJobExecutionContext context) => monitor.RefreshAsync(context.CancellationToken);
}
