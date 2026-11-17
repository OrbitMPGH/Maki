using Maki.Api.Auth;
using Maki.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// Read-only preview of how chapters split into parts by one source would fold into whole chapters
/// (<see cref="ChapterFoldPreviewService"/>). Nothing here changes a chapter; it exists so the rules
/// can be checked against a real library before anything acts on them.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/chapterfolds")]
public class ChapterFoldsController(ChapterFoldPreviewService previews) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Library(CancellationToken ct) => Ok(await previews.ForLibraryAsync(ct));

    [HttpGet("{seriesId:int}")]
    public async Task<IActionResult> Series(int seriesId, CancellationToken ct) =>
        await previews.ForSeriesAsync(seriesId, ct) is { } preview ? Ok(preview) : NotFound();
}
