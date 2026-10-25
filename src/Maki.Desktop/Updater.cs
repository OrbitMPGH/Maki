using Velopack;
using Velopack.Sources;

namespace Maki.Desktop;

/// <summary>
/// Downloads new releases from the GitHub release feed in the background and applies one only when
/// someone asks, from the tray or from Maki's own Settings. Stable releases only: a release
/// candidate is for people who chose to test it, and migrations make a step back expensive.
/// </summary>
internal sealed class Updater
{
    private const string Repository = "https://github.com/OrbitMPGH/Maki";

    private readonly UpdateManager? _manager;

    public Updater()
    {
        try
        {
            var manager = new UpdateManager(Source());
            // A build started from bin\ or an unpacked folder has no Velopack install to update.
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception)
        {
            _manager = null;
        }
    }

    public bool Enabled => _manager is not null;

    /// <summary>
    /// GitHub releases, or <c>MAKI_DESKTOP_UPDATE_FEED</c> (a folder or URL holding a vpk pack
    /// output) for trying an update end to end before anything is published.
    /// </summary>
    private static IUpdateSource Source() =>
        Environment.GetEnvironmentVariable("MAKI_DESKTOP_UPDATE_FEED") is { Length: > 0 } feed
            ? Directory.Exists(feed)
                ? new SimpleFileSource(new DirectoryInfo(feed))
                : new SimpleWebSource(feed)
            : new GithubSource(Repository, accessToken: null, prerelease: false);

    /// <summary>The downloaded version waiting for a restart, if any.</summary>
    public string? ReadyVersion => _manager?.UpdatePendingRestart?.Version.ToString();

    /// <summary>Checks the feed and downloads anything newer. Returns the version now ready, if any.</summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (_manager is null)
            return null;

        if (_manager.UpdatePendingRestart is { } pending)
            return pending.Version.ToString();

        var update = await _manager.CheckForUpdatesAsync();
        if (update is null)
            return null;

        await _manager.DownloadUpdatesAsync(update);
        return update.TargetFullRelease.Version.ToString();
    }

    /// <summary>Replaces this install with the downloaded version and starts it. Does not return.</summary>
    public void ApplyAndRestart()
    {
        if (_manager?.UpdatePendingRestart is { } pending)
            _manager.ApplyUpdatesAndRestart(pending);
    }
}
