using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/rootfolder")]
// Writes are admin-only: a root folder is a filesystem path the server will read and write. The
// list is open to anyone who may add a series, but scoped to the folders they were granted, so a
// non-admin learns no more of the host's layout than the folders their own library already lives in
// (issue #114: AddSeries without Admin had nowhere to point the add at).
public class RootFolderController(
    ILocalizer localizer, MakiDbContext db, IUserSnapshotCache snapshots, ICurrentUser currentUser)
    : ControllerBase
{
    [Authorize(Policy = Policies.AddSeries)]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var folders = currentUser.AllRootFolders || currentUser.Has(MakiPermission.Admin)
            ? await db.RootFolders.ToListAsync(ct)
            : await db.RootFolders.Where(f => currentUser.RootFolderIds.Contains(f.Id)).ToListAsync(ct);
        return Ok(folders.Select(ToDto));
    }

    [Authorize(Policy = Policies.Admin)]
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

    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var folder = await db.RootFolders.FindAsync([id], ct);
        if (folder is null)
        {
            return NotFound();
        }

        // Past the library scope: an admin granted only some folders must still be refused for one
        // whose series they cannot see, not hit the foreign key.
        if (await db.Series.IgnoreQueryFilters().AnyAsync(s => s.RootFolderId == id, ct))
        {
            return this.Conflict(localizer, "error.rootFolder.inUse");
        }

        // The grants cascade away, but a cached snapshot still lists this id until it is evicted.
        var grantees = await db.UserRootFolders
            .Where(g => g.RootFolderId == id)
            .Select(g => g.UserId)
            .ToListAsync(ct);
        db.RootFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
        foreach (var userId in grantees)
        {
            snapshots.Evict(userId);
        }

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
