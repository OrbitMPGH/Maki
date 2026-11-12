using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Parsing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace Maki.Api.Controllers;

/// <summary>
/// Import comics from any path on the server into a chosen series. Same permission as the library
/// import; which paths a caller may read is decided by <see cref="ManualImportService"/>: admins
/// anywhere, everyone else only inside their own root folders.
/// </summary>
[ApiController]
[Route("api/v1/manualimport")]
[Authorize(Policy = Policies.ImportLibrary)]
public class ManualImportController(
    ILocalizer localizer,
    ManualImportService manualImport,
    ISchedulerFactory schedulerFactory,
    ILogger<ManualImportController> logger) : ControllerBase
{
    public record PlanRequest(string Path, int SeriesId);

    public record ImportRequest(
        string Path, int SeriesId, ImportDecision Mode, IReadOnlyList<string>? SkipFiles = null, bool Move = false,
        string? ParsingFingerprint = null);

    [HttpGet("browse")]
    public async Task<IActionResult> Browse([FromQuery] string? path, CancellationToken ct)
    {
        try
        {
            return await manualImport.BrowseAsync(path, ct) is { } listing
                ? Ok(listing)
                : this.Fail(localizer, "error.rootFolder.doesNotExist", new { path });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return this.Fail(localizer, "error.rootFolder.notReadable", new { path });
        }
    }

    [HttpPost("plan")]
    public async Task<IActionResult> Plan([FromBody] PlanRequest request, CancellationToken ct)
    {
        if (await manualImport.ReadablePathAsync(request.Path, ct) is not { } full)
        {
            return this.Fail(localizer, "error.rootFolder.doesNotExist", new { path = request.Path });
        }

        var parsingFingerprint = NameParsingProfile.Current.Fingerprint;
        var plan = await manualImport.PlanAsync(full, request.SeriesId, ct);
        if (plan is null)
        {
            return NotFound();
        }

        return Ok(new
        {
            parsingFingerprint,
            path = full,
            plan.SeriesId,
            plan.SeriesTitle,
            plan.ReleaseName,
            plan.Files,
            error = plan.ErrorKey is not null ? localizer.Get(plan.ErrorKey, plan.ErrorArgs) : null,
            plan.HasConflicts,
            plan.NewChapterCount,
            plan.ReplacedFileCount,
            plan.Chapters,
        });
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportRequest request, CancellationToken ct)
    {
        if (request.Mode == ImportDecision.Reject)
        {
            return NoContent();
        }

        if (await manualImport.ReadablePathAsync(request.Path, ct) is not { } full)
        {
            return this.Fail(localizer, "error.rootFolder.doesNotExist", new { path = request.Path });
        }

        if (request.ParsingFingerprint is { } reviewed && reviewed != NameParsingProfile.Current.Fingerprint)
        {
            return this.Conflict(localizer, "error.queue.planStale");
        }

        var result = await manualImport.ImportAsync(
            full, request.SeriesId,
            request.Mode == ImportDecision.Replace ? TorrentImportMode.Replace : TorrentImportMode.SkipExisting,
            request.SkipFiles is { Count: > 0 } skip ? skip.ToHashSet(StringComparer.Ordinal) : null,
            request.Move ? ImportPlacement.Move : ImportPlacement.LinkOrCopy, ct);
        if (result is null)
        {
            return NotFound();
        }

        if (!result.Applied)
        {
            var rendered = localizer.Get(result.ErrorKey!, result.ErrorArgs) +
                           (result.Detail is null ? "" : $": {result.Detail}");
            return Conflict(new { error = rendered });
        }

        if (result.Imported > 0)
        {
            await ChapterFileMeasureJob.TriggerAsync(schedulerFactory, logger);
        }

        return Ok(new ImportDecisionResultDto(result.Imported, result.Linked, result.Skipped, result.Deleted));
    }
}
