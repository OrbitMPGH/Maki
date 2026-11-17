using Maki.Api.Auth;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace Maki.Api.Controllers;

/// <summary>
/// Read-only preview of how chapters split into parts by one source would fold into whole chapters
/// (<see cref="ChapterFoldPreviewService"/>). Nothing here changes a chapter; it exists so the rules
/// can be checked against a real library before anything acts on them.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/chapterfolds")]
public class ChapterFoldsController(ChapterFoldPreviewService previews, ISchedulerFactory schedulers) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Library(CancellationToken ct) => Ok(await previews.ForLibraryAsync(ct));

    [HttpGet("{seriesId:int}")]
    public async Task<IActionResult> Series(int seriesId, CancellationToken ct) =>
        await previews.ForSeriesAsync(seriesId, ct) is { } preview ? Ok(preview) : NotFound();

    /// <summary>Temporary: one source chapter's page hashes, to check a fold against the pages.</summary>
    [HttpGet("{seriesId:int}/pagehashes")]
    public async Task<IActionResult> PageHashes(
        int seriesId, [FromQuery] string mapping, [FromQuery] decimal number,
        [FromServices] ChapterFoldAuditService audit, CancellationToken ct) =>
        await audit.HashAsync(seriesId, mapping, number, ct) is { } hashes ? Ok(hashes) : NotFound();

    /// <summary>Starts fetching the page counts pending folds wait on, instead of at the next scheduled run.</summary>
    [HttpPost("count")]
    public async Task<IActionResult> Count(CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await scheduler.TriggerJob(ChapterPageCountJob.Key, ct);
        return Accepted();
    }
}
