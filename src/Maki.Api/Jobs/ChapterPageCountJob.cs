using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Fetches the page counts pending chapter folds wait on (<see cref="ChapterPageCountService"/>), then
/// the page hashes the merges they nominate wait on (<see cref="ChapterPageHashService"/>), a budget of
/// each per run. Also fired from the fold preview, so an admin does not wait for the timer.
/// </summary>
[DisallowConcurrentExecution]
public class ChapterPageCountJob(ChapterPageCountService counter, ChapterPageHashService hashes) : IJob
{
    public static readonly JobKey Key = new("chapter-page-count");

    public async Task Execute(IJobExecutionContext context)
    {
        await counter.RunAsync(ChapterPageCountService.RequestsPerRun, context.CancellationToken);
        await hashes.RunAsync(ChapterPageHashService.ChaptersPerRun, context.CancellationToken);
    }
}
