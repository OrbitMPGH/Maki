using Mangarr.Api.Configuration;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Mangarr.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemController(
    AppPaths paths,
    MangarrDbContext db,
    IAppSettings settings,
    MangaBakaDumpService mangaBakaDump) : ControllerBase
{
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var issues = new List<object>();

        var failingMappings = await db.SourceMappings
            .Where(m => m.Enabled && m.LastError != null)
            .Include(m => m.Series)
            .ToListAsync(ct);
        foreach (var mapping in failingMappings)
        {
            issues.Add(new
            {
                type = "sourceMapping",
                severity = "warning",
                message = $"{mapping.Series?.Title}: {mapping.SourceName} refresh failing — {mapping.LastError}"
            });
        }

        foreach (var folder in await db.RootFolders.ToListAsync(ct))
        {
            if (!Directory.Exists(folder.Path))
            {
                issues.Add(new
                {
                    type = "rootFolder",
                    severity = "error",
                    message = $"Root folder inaccessible: {folder.Path}"
                });
            }
        }

        // Only worth flagging when the series is actually monitored — a "Monitor: none" series
        // with no source isn't waiting on anything.
        var noMappings = await db.Series
            .Where(s => s.MonitorNewItems != NewChapterMonitorMode.None &&
                        !s.SourceMappings.Any(m => m.Enabled))
            .Select(s => s.Title)
            .ToListAsync(ct);
        foreach (var title in noMappings)
        {
            issues.Add(new
            {
                type = "series",
                severity = "warning",
                message = $"{title} is monitored but has no enabled source mappings"
            });
        }

        if (await settings.GetAsync(SettingKeys.MangaBakaUseLocalDb, ct) != "false")
        {
            var dump = await mangaBakaDump.GetStatusAsync(ct);
            var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            if (!dump.Present && uptime > TimeSpan.FromHours(1))
            {
                issues.Add(new
                {
                    type = "mangaBakaDump",
                    severity = "warning",
                    message = "MangaBaka local database not downloaded yet — metadata requests use the rate-limited API"
                });
            }
            else if (dump.Present && dump.RefreshedAt < DateTime.UtcNow.AddHours(-72))
            {
                issues.Add(new
                {
                    type = "mangaBakaDump",
                    severity = "warning",
                    message = $"MangaBaka local database is stale (last refresh {dump.RefreshedAt:yyyy-MM-dd HH:mm} UTC) — dump refresh may be failing"
                });
            }
        }

        return Ok(issues);
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            appName = "Mangarr",
            version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            osName = Environment.OSVersion.Platform.ToString(),
            configDir = paths.ConfigDir,
            startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
        });
    }
}
