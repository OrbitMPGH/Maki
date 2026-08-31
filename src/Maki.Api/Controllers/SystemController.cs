using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemController(
    AppPaths paths,
    HealthCheckService healthCheck,
    BackupService backups,
    UpdateCheckService updateCheck,
    ImageCacheRebuildService imageCache,
    ImageCacheRebuildStatus imageCacheStatus,
    ISchedulerFactory schedulerFactory,
    ICurrentUser currentUser,
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
            // Withheld from non-admins: it is an absolute path on the host, which tells a reader
            // account the deployment layout and nothing it has any use for.
            configDir = currentUser.Has(MakiPermission.Admin) ? paths.ConfigDir : null,
            startTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
        });
    }

    [HttpGet("update")]
    public IActionResult UpdateStatus() => Ok(updateCheck.GetStatus());

    /// <summary>
    /// Live rebuild status plus what the image caches occupy on disk. Admin-only: the byte counts
    /// and the missing-poster tally describe the instance, not the caller's library.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("image-cache")]
    public async Task<IActionResult> ImageCache(CancellationToken ct) =>
        Ok(new { status = imageCacheStatus.Snapshot(), usage = await imageCache.UsageAsync(ct) });

    public record RebuildImageCacheRequest(bool Force);

    /// <summary>
    /// Clears the reader thumbnail and source-preview caches, drops poster folders for series that
    /// no longer exist, and re-downloads posters: every one when <c>force</c> is set, otherwise only
    /// the ones that are missing or do not decode.
    /// <para>
    /// Fires the Quartz job and returns immediately — a full forced pass is one provider lookup and
    /// one image download per series, which is minutes on a large library. Poll
    /// <c>GET system/image-cache</c> for progress.
    /// </para>
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("image-cache/rebuild")]
    public async Task<IActionResult> RebuildImageCache(
        [FromBody] RebuildImageCacheRequest request, CancellationToken ct)
    {
        if (imageCacheStatus.Running)
        {
            return Ok(new { started = false, message = "A rebuild is already running" });
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var data = new JobDataMap { { ImageCacheRebuildJob.ForceKey, request.Force } };
        await scheduler.TriggerJob(ImageCacheRebuildJob.Key, data, ct);
        return Ok(new { started = true });
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("backups")]
    public IActionResult ListBackups() => Ok(backups.List());

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("backups")]
    public async Task<IActionResult> CreateBackup(CancellationToken ct) =>
        Ok(await backups.CreateAsync("manual", ct));

    [Authorize(Policy = Policies.Admin)]
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

    [Authorize(Policy = Policies.Admin)]
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

    [Authorize(Policy = Policies.Admin)]
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

    [Authorize(Policy = Policies.Admin)]
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
