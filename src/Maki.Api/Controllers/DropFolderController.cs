using System.Globalization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// The drop folder's settings, what it is waiting on, and a poll on demand. Admin only: the folder is
/// a path anywhere on the host, and the pending list names what is in it.
/// </summary>
[ApiController]
[Route("api/v1/dropfolder")]
[Authorize(Policy = Policies.Admin)]
public class DropFolderController(
    ILocalizer localizer, IAppSettings settings, DropFolderImportService dropFolder, MakiDbContext db) : ControllerBase
{
    /// <param name="Placement"><c>move</c> or <c>copy</c> (hardlink when the download setting allows it).</param>
    public record DropFolderSettings(bool Enabled, string? Path, int IntervalMinutes, string Placement);

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct) =>
        Ok(new DropFolderSettings(
            await settings.GetAsync(SettingKeys.ImportDropFolderEnabled, ct) == "true",
            await settings.GetAsync(SettingKeys.ImportDropFolderPath, ct),
            await dropFolder.IntervalMinutesAsync(ct),
            await settings.GetAsync(SettingKeys.ImportDropFolderPlacement, ct) == "move" ? "move" : "copy"));

    [HttpPut("settings")]
    public async Task<IActionResult> SetSettings([FromBody] DropFolderSettings request, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path.Trim();
        if (request.Enabled && path is null)
        {
            return this.Fail(localizer, "error.rootFolder.pathRequired");
        }

        if (path is not null)
        {
            if (!Directory.Exists(path))
            {
                return this.Fail(localizer, "error.rootFolder.doesNotExist", new { path });
            }

            // Polling a folder that holds the library would offer every series folder for import.
            var roots = await db.RootFolders.AsNoTracking().Select(r => r.Path).ToListAsync(ct);
            if (roots.Any(r => ManualImportService.IsWithin(r, path)))
            {
                return this.Fail(localizer, "error.libraryImport.folderOwnedByOtherSeries");
            }
        }

        await settings.SetAsync(SettingKeys.ImportDropFolderEnabled, request.Enabled ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.ImportDropFolderPath, path ?? "", ct);
        await settings.SetAsync(SettingKeys.ImportDropFolderIntervalMinutes,
            Math.Clamp(request.IntervalMinutes, 1, 1440).ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.ImportDropFolderPlacement, request.Placement == "move" ? "move" : "copy", ct);
        return await GetSettings(ct);
    }

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(Rendered(await dropFolder.StatusAsync(ct)));

    /// <summary>Polls now. A file still settles only once its size held across two polls.</summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        await dropFolder.TickAsync(force: true, ct);
        return Ok(Rendered(await dropFolder.StatusAsync(ct)));
    }

    /// <summary>The poll runs with no request language, so a failure's key is worded here, per reader.</summary>
    private DropFolderStatus Rendered(DropFolderStatus status) => status with
    {
        Pending = status.Pending
            .Select(p => p.Detail is null ? p : p with { Detail = localizer.Get(p.Detail, p.DetailArgs) })
            .ToList(),
    };
}
