using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/rootfolder")]
public class RootFolderController(MangarrDbContext db) : ControllerBase
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
            return BadRequest(new { error = "path is required" });
        }

        if (!Directory.Exists(folder.Path))
        {
            return BadRequest(new { error = $"Folder does not exist: {folder.Path}" });
        }

        if (await db.RootFolders.AnyAsync(f => f.Path == folder.Path, ct))
        {
            return Conflict(new { error = "Root folder already exists" });
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
            return Conflict(new { error = "Root folder is in use by one or more series" });
        }

        db.RootFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static object ToDto(RootFolder folder)
    {
        long? freeSpace = null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder.Path));
            if (root != null)
            {
                freeSpace = new DriveInfo(root).AvailableFreeSpace;
            }
        }
        catch
        {
            // Drive info is best-effort (network mounts etc.)
        }

        return new { folder.Id, folder.Path, FreeSpace = freeSpace, Accessible = Directory.Exists(folder.Path) };
    }
}
