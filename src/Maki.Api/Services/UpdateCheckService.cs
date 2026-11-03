using Maki.Api.Configuration;
using Maki.Api.Localization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Inbox;
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
    DateTimeOffset? CheckedAt)
{
    /// <summary><c>docker</c>, <c>desktop</c> or <c>bare</c>; decides how the UI says to update.</summary>
    public string Runtime { get; init; } = HostRuntime.Name(HostRuntime.Kind);

    /// <summary>Maki.Desktop has the update downloaded, so a restart applies it.</summary>
    public bool UpdateReady { get; init; }
}

/// <summary>
/// Polls the GitHub Releases API for a newer tag than the running build and raises a signal
/// (banner + Notifications connection). Docker and bare installs are notify-only. The desktop build
/// is updated by Maki.Desktop, which downloads the release itself; see <see cref="HostRestarter"/>.
/// </summary>
public class UpdateCheckService(
    IHttpClientFactory httpClientFactory,
    IAppSettings settings,
    NotificationService notifications,
    InboxService inbox,
    EventBroadcaster events,
    IMessageCatalog localizer,
    IUserLocaleResolver locales,
    ILogger<UpdateCheckService> logger)
{
    public const string HttpClientName = "github-releases";
    public const string ReleasePageClientName = "github-release-page";
    private const string Repo = "OrbitMPGH/Maki";

    public static readonly bool IsDocker = HostRuntime.Kind == HostRuntimeKind.Docker;

    private UpdateStatus _lastStatus = new(
        VersionInfo.Version, VersionInfo.IsDevBuild, IsDocker, false, null, null, null, null);

    /// <summary>Cached result of the last check — instant, no network call.</summary>
    public UpdateStatus GetStatus() => _lastStatus;

    /// <summary>
    /// Checks GitHub for a newer release; notifies (once per version) if one is found. Null when
    /// GitHub could not be asked, so a caller never reads a failed check as "up to date".
    /// </summary>
    public async Task<UpdateStatus?> CheckAsync(CancellationToken ct = default)
    {
        if (VersionInfo.IsDevBuild)
        {
            // No meaningful version to compare a dev build against — never nag.
            _lastStatus = _lastStatus with { CheckedAt = DateTimeOffset.UtcNow };
            return _lastStatus;
        }

        var release = await LatestFromApiAsync(ct) ?? await LatestFromReleasePageAsync(ct);
        if (release?.TagName is null)
        {
            return null;
        }

        var latestVersion = release.TagName.TrimStart('v', 'V');
        var isNewer = IsNewer(latestVersion, VersionInfo.Version);

        // The release page fallback carries no notes; keep the ones an earlier check fetched.
        var notes = release.Body
            ?? (string.Equals(_lastStatus.LatestVersion, latestVersion, StringComparison.OrdinalIgnoreCase)
                ? _lastStatus.ReleaseNotes
                : null);

        _lastStatus = new UpdateStatus(
            VersionInfo.Version, false, IsDocker, isNewer,
            latestVersion, release.HtmlUrl, notes, DateTimeOffset.UtcNow);

        if (isNewer)
        {
            var lastNotified = await settings.GetAsync(SettingKeys.UpdatesLastNotifiedVersion, ct);
            if (!string.Equals(lastNotified, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                await settings.SetAsync(SettingKeys.UpdatesLastNotifiedVersion, latestVersion, ct);
                logger.LogInformation("Update available: {Latest} (running {Current})", latestVersion, VersionInfo.Version);
                var locale = await locales.DefaultAsync(ct);
                notifications.Dispatch(NotificationEventType.UpdateAvailable, new NotificationMessage(
                    NotificationEventType.UpdateAvailable,
                    Title: localizer.GetFor(locale, "notify.update.available.title"),
                    Body: localizer.GetFor(locale, "notify.update.available.body", new
                    {
                        latest = latestVersion,
                        current = VersionInfo.Version,
                    }),
                    Url: release.HtmlUrl));

                // Not the release URL: the inbox's Url is a path inside the SPA, and the Updates
                // card is where an admin acts on this anyway.
                inbox.Raise(InboxEventType.UpdateAvailable, new InboxMessage(
                        Key: "inbox.update.available",
                        Params: InboxMessage.Args(new { latest = latestVersion, current = VersionInfo.Version }),
                        Url: "/settings?tab=system&s=updates"),
                    InboxAudience.Admins);

                _ = events.UpdateAvailable(latestVersion, release.HtmlUrl);
            }
        }

        return _lastStatus;
    }

    private async Task<GitHubRelease?> LatestFromApiAsync(CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            return await client.GetFromJsonAsync<GitHubRelease>($"repos/{Repo}/releases/latest", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Update check failed against the GitHub API, trying the release page");
            return null;
        }
    }

    /// <summary>
    /// The unauthenticated API allows 60 requests an hour per public IP, shared with everything else
    /// on that network. The release page's redirect to the latest tag is not counted against it.
    /// </summary>
    private async Task<GitHubRelease?> LatestFromReleasePageAsync(CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ReleasePageClientName);
            using var request = new HttpRequestMessage(HttpMethod.Head, $"{Repo}/releases/latest");
            using var response = await client.SendAsync(request, ct);
            var tag = TagFromReleaseLocation(response.Headers.Location);
            if (tag is null)
            {
                logger.LogWarning("Update check failed: the release page answered {Status} without a release tag",
                    (int)response.StatusCode);
                return null;
            }

            return new GitHubRelease(tag, $"https://github.com/{Repo}/releases/tag/{Uri.EscapeDataString(tag)}", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Update check failed");
            return null;
        }
    }

    internal static string? TagFromReleaseLocation(Uri? location)
    {
        const string marker = "/releases/tag/";
        if (location is null)
        {
            return null;
        }

        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?', '#')[0];
        var at = path.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var tag = Uri.UnescapeDataString(path[(at + marker.Length)..].TrimEnd('/'));
        return tag.Length == 0 || tag.Contains('/') ? null : tag;
    }

    /// <summary>Tags/versions here are plain "X.Y.Z" (see distribution/release.ps1) — strip any
    /// leftover prerelease/build suffix before comparing as a System.Version.</summary>
    internal static bool IsNewer(string latest, string current)
    {
        var latestCore = latest.Split('-', '+')[0];
        var currentCore = current.Split('-', '+')[0];
        if (!Version.TryParse(latestCore, out var l) || !Version.TryParse(currentCore, out var c))
        {
            return false;
        }

        // The final release of a build someone is testing is an update for them.
        var currentIsPrerelease = current.Split('+')[0].Contains('-');
        var latestIsPrerelease = latest.Split('+')[0].Contains('-');
        return l > c || (l == c && currentIsPrerelease && !latestIsPrerelease);
    }

    private record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body);
}
