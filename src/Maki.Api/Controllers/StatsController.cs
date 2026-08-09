using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// Instance-level statistics. Reading history lives on <see cref="RewindController"/> and
/// progression on <c>GamificationController</c>; this is the collection itself.
/// <para>
/// No <c>[Authorize]</c>, so the fail-closed fallback policy applies and any signed-in user
/// reaches it. No permission bit and no <c>userId</c>: there is nothing per-user to resolve, and
/// what a caller may see is already decided by the root-folder query filters.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/stats")]
public class StatsController(LibraryCompositionService library) : ControllerBase
{
    [HttpGet("library")]
    public async Task<IActionResult> Library(CancellationToken ct)
    {
        return Ok(await library.GetAsync(ct));
    }
}
