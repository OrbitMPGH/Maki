using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Notifications;

namespace Maki.Api.Services;

public record UpdateStatus(
    string CurrentVersion,
    bool IsDevBuild,
    bool IsDocker,
    bool UpdateAvailable,
    string? LatestVersion,
    string? ReleaseUrl,
    string? ReleaseNotes,
    DateTimeOffset? CheckedAt);

/// <summary>
/// Polls the GitHub Releases API for a newer tag than the running build. Both Docker and bare
/// installs are notify-only today: GitHub releases here are tag + changelog only (see
/// distribution/release.ps1), no per-OS binary is published for a self-updater to fetch and
/// swap in, so this only ever raises a signal (banner + Notifications connection).
/// </summary>
public class UpdateCheckService(
    IHttpClientFactory httpClientFactory,
    IAppSettings settings,
    NotificationService notifications,
    EventBroadcaster events,
    ILogger<UpdateCheckService> logger)
{
    public const string HttpClientName = "github-releases";
    private const string Repo = "OrbitMPGH/Maki";

    /// <summary>Docker sets MAKI_RUNTIME=docker in the image; /.dockerenv is a fallback for
    /// other container setups that don't.</summary>
    public static readonly bool IsDocker =
        Environment.GetEnvironmentVariable("MAKI_RUNTIME") == "docker" || File.Exists("/.dockerenv");

    private UpdateStatus _lastStatus = new(
        VersionInfo.Version, VersionInfo.IsDevBuild, IsDocker, false, null, null, null, null);

    /// <summary>Cached result of the last check — instant, no network call.</summary>
    public UpdateStatus GetStatus() => _lastStatus;

    /// <summary>Checks GitHub for a newer release; notifies (once per version) if one is found.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        if (VersionInfo.IsDevBuild)
        {
            // No meaningful version to compare a dev build against — never nag.
            _lastStatus = _lastStatus with { CheckedAt = DateTimeOffset.UtcNow };
            return _lastStatus;
        }

        GitHubRelease? release;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            release = await client.GetFromJsonAsync<GitHubRelease>($"repos/{Repo}/releases/latest", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed");
            return _lastStatus;
        }

        if (release?.TagName is null)
        {
            return _lastStatus;
        }

        var latestVersion = release.TagName.TrimStart('v', 'V');
        var isNewer = IsNewer(latestVersion, VersionInfo.Version);

        _lastStatus = new UpdateStatus(
            VersionInfo.Version, false, IsDocker, isNewer,
            latestVersion, release.HtmlUrl, release.Body, DateTimeOffset.UtcNow);

        if (isNewer)
        {
            var lastNotified = await settings.GetAsync(SettingKeys.UpdatesLastNotifiedVersion, ct);
            if (!string.Equals(lastNotified, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                await settings.SetAsync(SettingKeys.UpdatesLastNotifiedVersion, latestVersion, ct);
                logger.LogInformation("Update available: {Latest} (running {Current})", latestVersion, VersionInfo.Version);
                notifications.Dispatch(NotificationEventType.UpdateAvailable, new NotificationMessage(
                    NotificationEventType.UpdateAvailable,
                    Title: "Update available",
                    Body: $"Maki {latestVersion} is available (running {VersionInfo.Version}).",
                    Url: release.HtmlUrl));
                _ = events.UpdateAvailable(latestVersion, release.HtmlUrl);
            }
        }

        return _lastStatus;
    }

    /// <summary>Tags/versions here are plain "X.Y.Z" (see distribution/release.ps1) — strip any
    /// leftover prerelease/build suffix before comparing as a System.Version.</summary>
    private static bool IsNewer(string latest, string current)
    {
        var latestCore = latest.Split('-', '+')[0];
        var currentCore = current.Split('-', '+')[0];
        return Version.TryParse(latestCore, out var l) && Version.TryParse(currentCore, out var c) && l > c;
    }

    private record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body);
}
