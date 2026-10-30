using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>Refreshes every custom source repository's index once a day and applies the updates its repository keeps up to date.</summary>
[DisallowConcurrentExecution]
public class CustomSourceRepositoryJob(CustomRepositoryService repositories) : IJob
{
    public Task Execute(IJobExecutionContext context) => repositories.RunDailyAsync(context.CancellationToken);
}
