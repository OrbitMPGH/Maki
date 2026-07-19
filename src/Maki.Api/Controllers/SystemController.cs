using Maki.Api.Configuration;
using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemController(
    AppPaths paths,
    HealthCheckService healthCheck,
    BackupService backups,
    UpdateCheckService updateCheck,
    IHostApplicationLifetime lifetime,
    ILogger<SystemController> logger) : ControllerBase
{
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct) =>
        Ok((await healthCheck.GetIssuesAsync(ct))
            .Select(i => new { type = i.Type, severity = i.Severity, message = i.Message }));

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            appName = "Maki",
            version = VersionInfo.Version,
            commit = VersionInfo.Commit,
            isDevBuild = VersionInfo.IsDevBuild,
            osName = Environment.OSVersion.Platform.ToString(),
            configDir = paths.ConfigDir,
            startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
        });
    }

    [HttpGet("update")]
    public IActionResult UpdateStatus() => Ok(updateCheck.GetStatus());

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
