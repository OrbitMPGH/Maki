using Maki.Api.Configuration;

namespace Maki.Api.Services;

/// <summary>
/// Ends the process on purpose so whatever supervises it starts it again: after a restore, a change
/// to config.json, or with an update waiting. The exit code tells Maki.Desktop which of those it is;
/// Docker and systemd only see a non-zero exit and restart.
/// </summary>
public class HostRestarter(IHostApplicationLifetime lifetime, AppPaths paths, ILogger<HostRestarter> logger)
{
    public void Restart(string reason) => Stop(HostRuntime.RestartExitCode, reason);

    /// <summary>False unless Maki.Desktop has an update downloaded for a version newer than this one.</summary>
    public bool TryRestartForUpdate()
    {
        if (ReadyUpdateVersion() is not { } version)
            return false;

        Stop(HostRuntime.ApplyUpdateExitCode, $"applying the update to {version}");
        return true;
    }

    /// <summary>The version Maki.Desktop has downloaded and can apply on the next restart, if any.</summary>
    public string? ReadyUpdateVersion()
    {
        if (HostRuntime.Kind != HostRuntimeKind.Desktop)
            return null;

        try
        {
            var file = Path.Combine(paths.ConfigDir, HostRuntime.UpdateReadyFile);
            if (!File.Exists(file))
                return null;

            var version = File.ReadAllText(file).Trim();
            return UpdateCheckService.IsNewer(version, VersionInfo.Version) ? version : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Stop(int exitCode, string reason)
    {
        logger.LogWarning("Stopping Maki ({Reason}) so it starts again", reason);
        Environment.ExitCode = exitCode;
        _ = Task.Run(async () =>
        {
            // Long enough for the response that asked for this to reach the browser.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            lifetime.StopApplication();
        });
    }
}
