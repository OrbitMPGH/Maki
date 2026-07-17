using Mangarr.Api.Configuration;
using Mangarr.Api.Services;
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
    MangaBakaDumpService mangaBakaDump,
    BackupService backups,
    IHostApplicationLifetime lifetime,
    ILogger<SystemController> logger) : ControllerBase
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
            version = VersionInfo.Version,
            commit = VersionInfo.Commit,
            isDevBuild = VersionInfo.IsDevBuild,
            osName = Environment.OSVersion.Platform.ToString(),
            configDir = paths.ConfigDir,
            startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
        });
    }

    [HttpGet("backups")]
    public IActionResult ListBackups() => Ok(backups.List());

    [HttpPost("backups")]
    public async Task<IActionResult> CreateBackup(CancellationToken ct) =>
        Ok(await backups.CreateAsync("manual", ct));

    [HttpGet("backups/{name}")]
    public IActionResult DownloadBackup(string name)
    {
        try
        {
            return PhysicalFile(backups.PathFor(name), "application/zip", name);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpDelete("backups/{name}")]
    public IActionResult DeleteBackup(string name)
    {
        try
        {
            backups.Delete(name);
            return NoContent();
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("backups/{name}/restore")]
    public async Task<IActionResult> RestoreBackup(string name, CancellationToken ct)
    {
        try
        {
            await backups.StagePendingRestoreFromFileAsync(name, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException)
        {
            return BadRequest(new { message = ex.Message });
        }

        ScheduleRestart();
        return Accepted(new { message = "Restore staged. Restarting to apply." });
    }

    [HttpPost("backups/restore-upload")]
    [RequestSizeLimit(1_073_741_824)] // 1 GiB
    public async Task<IActionResult> RestoreUpload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded." });

        try
        {
            await using var stream = file.OpenReadStream();
            await backups.StagePendingRestoreFromUploadAsync(stream, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            return BadRequest(new { message = ex.Message });
        }

        ScheduleRestart();
        return Accepted(new { message = "Restore staged. Restarting to apply." });
    }

    /// <summary>Stops the app shortly after the response flushes so the staged restore is applied on
    /// the next boot. Only auto-recovers under a supervisor (Docker restart policy, systemd).</summary>
    private void ScheduleRestart()
    {
        logger.LogWarning("Restore staged — stopping application so it restarts into the restored data");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            lifetime.StopApplication();
        });
    }
}
