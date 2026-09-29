using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/chapter-files")]
public class ChapterFilesController(MakiDbContext db) : ControllerBase
{
    /// <summary>
    /// "Protect from upgrades". A trusted file is never replaced by the upgrader. 404 for a file the
    /// caller cannot see, which the query filters decide.
    /// </summary>
    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("{id:int}/trusted")]
    public async Task<IActionResult> SetTrusted(int id, [FromBody] SetTrustedRequest request, CancellationToken ct)
    {
        var updated = await db.ChapterFiles
            .Where(f => f.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Trusted, request.Trusted), ct);
        return updated == 0 ? NotFound() : Ok(new TrustedDto(request.Trusted));
    }
}
