namespace Maki.Api.Configuration;

public enum HostRuntimeKind
{
    /// <summary>Started by hand, from an IDE, or from a tarball without a service manager.</summary>
    Bare,
    Docker,
    /// <summary>Started by Maki.Desktop, the Windows tray app, which restarts and updates it.</summary>
    Desktop,
}

/// <summary>
/// How this process was started, and therefore who brings it back after it stops on purpose. A
/// restore and an applied update both end the process; whether Maki comes back by itself depends
/// entirely on what is supervising it.
/// </summary>
public static class HostRuntime
{
    /// <summary>Asks Maki.Desktop to start Maki again. Anything else treats it as a plain failure,
    /// which still brings it back under Docker's restart policy or a systemd <c>Restart=</c>.</summary>
    public const int RestartExitCode = 10;

    /// <summary>Asks Maki.Desktop to apply the update it has downloaded, then start the new version.</summary>
    public const int ApplyUpdateExitCode = 11;

    /// <summary>Written by Maki.Desktop into the config directory once an update is downloaded and
    /// ready to apply. It holds the version, so a stale file from an older download is ignored.</summary>
    public const string UpdateReadyFile = "update-ready";

    public static HostRuntimeKind Kind { get; } = Detect(
        Environment.GetEnvironmentVariable("MAKI_RUNTIME"), File.Exists("/.dockerenv"));

    /// <summary>
    /// Whether a stop is followed by a start without anyone doing it by hand. systemd sets
    /// INVOCATION_ID for every unit it starts, which covers the Linux tarball's sample unit.
    /// </summary>
    public static bool IsSupervised { get; } = Kind != HostRuntimeKind.Bare
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"));

    internal static HostRuntimeKind Detect(string? variable, bool dockerEnv) => variable?.Trim().ToLowerInvariant() switch
    {
        "docker" => HostRuntimeKind.Docker,
        "desktop" => HostRuntimeKind.Desktop,
        _ => dockerEnv ? HostRuntimeKind.Docker : HostRuntimeKind.Bare,
    };

    public static string Name(HostRuntimeKind kind) => kind.ToString().ToLowerInvariant();
}
