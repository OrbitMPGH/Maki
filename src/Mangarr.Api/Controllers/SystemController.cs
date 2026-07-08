using Mangarr.Api.Configuration;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemController(AppPaths paths, MangarrDbContext db) : ControllerBase
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

        var noMappings = await db.Series
            .Where(s => s.Monitored && !s.SourceMappings.Any(m => m.Enabled))
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
