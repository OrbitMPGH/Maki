using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Fetches the page counts pending chapter folds wait on (<see cref="ChapterPageCountService"/>), a
/// budget per run. Also fired from the fold preview, so an admin does not wait for the timer.
/// </summary>
[DisallowConcurrentExecution]
public class ChapterPageCountJob(ChapterPageCountService counter) : IJob
{
    public static readonly JobKey Key = new("chapter-page-count");

    public Task Execute(IJobExecutionContext context) =>
        counter.RunAsync(ChapterPageCountService.RequestsPerRun, context.CancellationToken);
}
