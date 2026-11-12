using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
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

    /// <summary>
    /// The subfolders of <paramref name="path"/>, for the folder picker. No path lists the drives on
    /// Windows and <c>/</c> elsewhere. A parent of <c>""</c> means "back to the drive list".
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (OperatingSystem.IsWindows())
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new FolderEntry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName))
                    .ToList();
                return Ok(new FolderListing("", null, drives));
            }

            path = "/";
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return this.Fail(localizer, "error.rootFolder.doesNotExist", new { path });
        }

        if (!Directory.Exists(full))
        {
            return this.Fail(localizer, "error.rootFolder.doesNotExist", new { path });
        }

        List<FolderEntry> entries;
        try
        {
            entries = new DirectoryInfo(full)
                .EnumerateDirectories("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                })
                .Select(d => new FolderEntry(d.Name, d.FullName))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return this.Fail(localizer, "error.rootFolder.notReadable", new { path = full });
        }

        var parent = Directory.GetParent(full)?.FullName ?? (OperatingSystem.IsWindows() ? "" : null);
        return Ok(new FolderListing(full, parent, entries));
    }

    public record FolderEntry(string Name, string Path);

    public record FolderListing(string Path, string? Parent, List<FolderEntry> Folders);

    public record ChangePathRequest(string? Path);

    /// <summary>
    /// Dry run of <see cref="ChangePath"/>: checks the new path exists and is readable, counts the
    /// series folders found and missing under it, and says what would refuse or need a look.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id:int}/path/check")]
    public async Task<IActionResult> CheckPath(int id, [FromBody] ChangePathRequest request,
        [FromServices] RootFolderPathService paths, CancellationToken ct) =>
        Answer(await paths.CheckAsync(id, request.Path, ct), check => Ok(check));

    /// <summary>
    /// Points the root folder at a new path, for a re-mounted share or a moved library. Files are not
    /// moved. Refused while downloads, imports, bulk moves or health scans are running against it.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id:int}/path")]
    public async Task<IActionResult> ChangePath(int id, [FromBody] ChangePathRequest request,
        [FromServices] RootFolderPathService paths, CancellationToken ct) =>
        Answer(await paths.ApplyAsync(id, request.Path, ct), check => check.Busy.Count > 0
            ? Conflict(new { busy = check.Busy })
            : Ok(ToDto(new RootFolder { Id = check.Id, Path = check.Path })));

    private IActionResult Answer(RootFolderPathOutcome outcome, Func<RootFolderPathCheck, IActionResult> checkedResult) =>
        outcome switch
        {
            { NotFound: true } => NotFound(),
            { Refusal: { Conflict: true } refusal } => this.Conflict(localizer, refusal.Key, refusal.Args),
            { Refusal: { } refusal } => this.Fail(localizer, refusal.Key, refusal.Args),
            _ => checkedResult(outcome.Check!),
        };

    /// <param name="MinFreeBytes">Null for the default, 0 to never hold downloads for this folder.</param>
    public record MinFreeRequest(long? MinFreeBytes);

    /// <summary>
    /// The free space below which new downloads into this folder wait (<see cref="DownloadDiskHold"/>).
    /// The next claim reads the folder's free space again rather than waiting out the cached reading.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id:int}/minfree")]
    public async Task<IActionResult> SetMinFree(int id, [FromBody] MinFreeRequest request,
        [FromServices] DownloadDiskHold? diskHold, CancellationToken ct)
    {
        if (request.MinFreeBytes < 0)
        {
            return this.Fail(localizer, "error.rootFolder.minFreeNegative");
        }

        var folder = await db.RootFolders.FindAsync([id], ct);
        if (folder is null)
        {
            return NotFound();
        }

        folder.MinFreeBytes = request.MinFreeBytes;
        await db.SaveChangesAsync(ct);
        diskHold?.Expire();
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
        var users = db.Series.IgnoreQueryFilters().Where(s => s.RootFolderId == id);
        if (await users.Active().AnyAsync(ct))
        {
            return this.Conflict(localizer, "error.rootFolder.inUse");
        }

        if (await users.CountAsync(ct) is > 0 and var archived)
        {
            return this.Conflict(localizer, "error.rootFolder.inUseArchived", new { count = archived });
        }

        // The grants cascade away, but a cached snapshot still lists this id until it is evicted.
        var grantees = await db.UserRootFolders
            .Where(g => g.RootFolderId == id)
            .Select(g => g.UserId)
            .ToListAsync(ct);
        // Health rows, operations and scans have no foreign key to this table. An operation mid
        // apply or delete is left for startup recovery; one waiting on a download never gets it,
        // since the series and so its queue rows are gone.
        await HealthOperationService.MutationGate.WaitAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var files = db.HealthFiles.Where(f => f.RootFolderId == id);
            await db.HealthOperations
                .Where(o => o.Status != "completed" && o.Status != "failed" && o.Status != "cancelled"
                    && o.Status != "applying" && o.Status != "deleting" && files.Any(f => f.Id == o.FileId))
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, "cancelled").SetProperty(o => o.FinishedAt, DateTime.UtcNow), ct);
            await db.HealthScans.Where(s => s.RootFolderId == id && (s.Status == "pending" || s.Status == "running"))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, "cancelled"), ct);
            await HealthInventory.RetireAsync(db, files, ct);
            db.RootFolders.Remove(folder);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            HealthOperationService.MutationGate.Release();
        }

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
        folder.MinFreeBytes,
        DefaultMinFreeBytes = RootFolder.DefaultMinFreeBytes,
    };
}
