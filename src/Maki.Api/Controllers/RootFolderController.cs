using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/rootfolder")]
// Admin-only: a root folder is a filesystem path the server will read and write, and listing them
// discloses the host's directory layout.
[Authorize(Policy = Policies.Admin)]
public class RootFolderController(ILocalizer localizer, MakiDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var folders = await db.RootFolders.ToListAsync(ct);
        return Ok(folders.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] RootFolder folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder.Path))
        {
            return this.Fail(localizer, "error.rootFolder.pathRequired");
        }

        if (!Directory.Exists(folder.Path))
        {
            return this.Fail(localizer, "error.rootFolder.doesNotExist", new { path = folder.Path });
        }

        if (await db.RootFolders.AnyAsync(f => f.Path == folder.Path, ct))
        {
            return this.Conflict(localizer, "error.rootFolder.alreadyExists");
        }

        db.RootFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(folder));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var folder = await db.RootFolders.FindAsync([id], ct);
        if (folder is null)
        {
            return NotFound();
        }

        if (await db.Series.AnyAsync(s => s.RootFolderId == id, ct))
        {
            return this.Conflict(localizer, "error.rootFolder.inUse");
        }

        db.RootFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static object ToDto(RootFolder folder) => new
    {
        folder.Id,
        folder.Path,
        FreeSpace = DiskSpace.AvailableFor(folder.Path),
        Accessible = Directory.Exists(folder.Path),
    };
}
