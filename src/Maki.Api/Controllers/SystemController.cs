using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemController(
    AppPaths paths,
    BackupService backups,
    UpdateCheckService updateCheck,
    MemoryDiagnostics memory,
    ImageCacheRebuildService imageCache,
    ImageCacheRebuildStatus imageCacheStatus,
    ISchedulerFactory schedulerFactory,
    ICurrentUser currentUser,
    HostRestarter restarter,
    ConfigFileProvider configFile,
    Maki.Api.Localization.ILocalizer localizer,
    ILogger<SystemController> logger) : ControllerBase
{
    private const long RestoreUploadLimit = 1_073_741_824; // 1 GiB

    /// <summary>
    /// Open health issues for the header indicator.
    /// </summary>
    /// <remarks>
    /// Acknowledged checks are excluded. Acknowledging is the user saying "I have seen this and it
    /// is not going to change" - a service they do not run, a drive they know is small - and if the
    /// badge kept counting it anyway the acknowledgement would mean nothing. The check itself stays
    /// in the workspace, and any change of status clears the flag so a new problem is never
    /// inherited as already-seen.
    /// </remarks>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var rows = await HttpContext.RequestServices.GetRequiredService<Maki.Data.MakiDbContext>().HealthChecks
            .Where(HealthTransitions.Unattended)
            .Select(i => new { i.Category, i.Status, i.MessageKey, i.ParamsJson, i.Message }).ToListAsync(ct);
        return Ok(rows.Select(i => new
        {
            type = i.Category,
            severity = i.Status,
            message = i.MessageKey is { Length: > 0 } key
                ? localizer.Get(key, HealthMonitor.HealthParams(i.ParamsJson))
                : i.Message,
        }));
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
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
            startTime = process.StartTime.ToUniversalTime()
        });
    }

    [HttpGet("update")]
    public IActionResult UpdateStatus() =>
        Ok(updateCheck.GetStatus() with { UpdateReady = restarter.ReadyUpdateVersion() is not null });

    /// <summary>Restarts into the update Maki.Desktop has downloaded. Desktop build only.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("update/apply")]
    public IActionResult ApplyUpdate() =>
        restarter.TryRestartForUpdate()
            ? Accepted()
            : this.Fail(localizer, "error.system.noUpdateReady");

    /// <summary>How this instance runs and where it listens, for the network access setting.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("host")]
    public IActionResult Host() => Ok(HostInfo());

    /// <summary>
    /// Switches between listening on this machine only and on every network. Written to config.json
    /// and applied on the next start, because Kestrel binds once.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("host/network")]
    public IActionResult SetNetworkAccess([FromBody] NetworkAccessRequest request)
    {
        // Inside a container, localhost is the container itself: nothing outside could reach Maki.
        if (HostRuntime.Kind == HostRuntimeKind.Docker)
            return this.Fail(localizer, "error.system.networkAccessDocker");

        configFile.UpdateOnDisk(c => c.BindAddress = request.AllowNetwork ? "*" : "localhost");
        return Ok(HostInfo());
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("restart")]
    public IActionResult Restart()
    {
        if (!HostRuntime.IsSupervised)
            return this.Fail(localizer, "error.system.restartUnsupervised");

        restarter.Restart("an administrator asked for a restart");
        return Accepted();
    }

    public record NetworkAccessRequest(bool AllowNetwork);

    private object HostInfo()
    {
        var configured = configFile.ReadOnDisk().BindAddress;
        return new
        {
            runtime = HostRuntime.Name(HostRuntime.Kind),
            supervised = HostRuntime.IsSupervised,
            port = configFile.Config.Port,
            bindAddress = configFile.Config.BindAddress,
            // Differs from bindAddress after a change that is waiting for a restart.
            configuredBindAddress = configured,
            networkAccess = IsNetworkWide(configFile.Config.BindAddress),
            configuredNetworkAccess = IsNetworkWide(configured),
        };
    }

    private static bool IsNetworkWide(string bindAddress)
    {
        var address = bindAddress.Trim();
        return !address.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !(System.Net.IPAddress.TryParse(address, out var ip) && System.Net.IPAddress.IsLoopback(ip));
    }

    /// <summary>
    /// Where the process's memory is, split into managed heap, native, and the kernel's own
    /// accounting, plus which discovery artifacts are currently loaded.
    /// </summary>
    /// <remarks>
    /// Admin-only: it describes the host rather than the caller's library, and the cgroup limit
    /// tells a reader account how the deployment is provisioned.
    /// <para>
    /// <c>collect=true</c> forces a blocking compacting collection first and reports the managed
    /// total from both before and after it, which is the only way to tell a genuinely retained
    /// hundred megabytes from a hundred megabytes of garbage nothing has needed to reclaim yet.
    /// It stalls every request for the length of the collection, so it is opt-in.
    /// </para>
    /// </remarks>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("memory")]
    public IActionResult Memory([FromQuery] bool collect = false) => Ok(memory.Snapshot(collect));

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
            return Ok(new { started = false, message = localizer.Get("error.system.rebuildRunning") });
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
    public async Task<IActionResult> CreateBackup(CancellationToken ct)
    {
        try
        {
            return Ok(await backups.CreateAsync("manual", ct));
        }
        catch (BackupCreateException ex)
        {
            return this.Fail(localizer, ex.Key);
        }
    }

    [Authorize(Policy = Policies.Admin)]
    [CookieSessionOnly]
    [HttpGet("backups/{name}")]
    public IActionResult DownloadBackup(string name)
    {
        try
        {
            return PhysicalFile(backups.PathFor(name), "application/zip", name);
        }
        catch (ArgumentException)
        {
            return this.NotFoundMessage(localizer, "error.system.backupNameInvalid");
        }
        catch (FileNotFoundException)
        {
            return this.NotFoundMessage(localizer, "error.system.backupNotFound", new { name });
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
        catch (ArgumentException)
        {
            return this.NotFoundMessage(localizer, "error.system.backupNameInvalid");
        }
        catch (FileNotFoundException)
        {
            return this.NotFoundMessage(localizer, "error.system.backupNotFound", new { name });
        }
    }

    [Authorize(Policy = Policies.Admin)]
    [CookieSessionOnly]
    [HttpPost("backups/{name}/restore")]
    public async Task<IActionResult> RestoreBackup(string name, CancellationToken ct)
    {
        try
        {
            await backups.StagePendingRestoreFromFileAsync(name, ct);
        }
        catch (BackupRestoreException ex)
        {
            return this.Fail(localizer, ex.Key, ex.Args);
        }
        catch (ArgumentException)
        {
            return this.NotFoundMessage(localizer, "error.system.backupNameInvalid");
        }
        catch (FileNotFoundException)
        {
            return this.NotFoundMessage(localizer, "error.system.backupNotFound", new { name });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not stage the restore of backup {Name}", name);
            return this.Fail(localizer, "error.system.restoreFailed");
        }

        return RestartForRestore();
    }

    [Authorize(Policy = Policies.Admin)]
    [CookieSessionOnly]
    [HttpPost("backups/restore-upload")]
    [RequestSizeLimit(RestoreUploadLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = RestoreUploadLimit)]
    public async Task<IActionResult> RestoreUpload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return this.Fail(localizer, "error.system.noFileUploaded");

        try
        {
            await using var stream = file.OpenReadStream();
            await backups.StagePendingRestoreFromUploadAsync(stream, ct);
        }
        catch (BackupRestoreException ex)
        {
            return this.Fail(localizer, ex.Key, ex.Args);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            logger.LogWarning(ex, "Could not stage the uploaded restore");
            return this.Fail(localizer, "error.system.restoreFailed");
        }

        return RestartForRestore();
    }

    /// <summary>Stops the app shortly after the response flushes so the staged restore is applied on
    /// the next boot. Without a supervisor it stays stopped, and the message says to start it again.</summary>
    private IActionResult RestartForRestore()
    {
        restarter.Restart("a restore was staged");
        return Accepted(new
        {
            message = localizer.Get(HostRuntime.IsSupervised
                ? "error.system.restoreStaged"
                : "error.system.restoreStagedManualStart")
        });
    }
}
