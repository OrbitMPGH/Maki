using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/release")]
// Both actions: the search hits the instance's Prowlarr indexers, and the grab pushes a torrent to
// qBittorrent. Neither is something a read-only account should reach.
[Authorize(Policy = Policies.DownloadChapters)]
public class ReleaseController(ReleaseService releaseService) : ControllerBase
{
    public record GrabRequest(int SeriesId, ReleaseDto Release);

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] int seriesId, [FromQuery] string? query, CancellationToken ct)
    {
        try
        {
            return Ok(await releaseService.SearchAsync(seriesId, query, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("grab")]
    public async Task<IActionResult> Grab([FromBody] GrabRequest request, CancellationToken ct)
    {
        try
        {
            var item = await releaseService.GrabAsync(request.SeriesId, request.Release, ct);
            return Ok(new { queueItemId = item.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
