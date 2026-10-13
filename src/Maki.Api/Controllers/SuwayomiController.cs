using System.Net;
using System.Text.RegularExpressions;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Maki.Core.Download;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Sources.Suwayomi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// The optional Suwayomi integration: its connection, the snapshot of its installed sources, the
/// chapter language of the ones that mix languages, and a relay for its thumbnails. Kept out of
/// <c>SettingsController</c> so that controller's constructor does not grow for an add-on. Which
/// Suwayomi sources Maki uses is the ordinary per-source switch in Manage sources. The settings
/// actions are admin-only one by one, the same rule <c>SettingsController</c> follows, because they
/// reach the server with the stored credentials. The thumbnail relay is for any signed-in reader,
/// like the cover proxy.
/// </summary>
[ApiController]
[Route("api/v1")]
public partial class SuwayomiController(
    ILocalizer localizer,
    IAppSettings settings,
    SuwayomiSettings suwayomiSettings,
    SuwayomiClient client,
    SuwayomiTokenStore tokens,
    SuwayomiMangaIdCache cache,
    SuwayomiConnectionTester tester,
    SourceRegistry sourceRegistry,
    SourceAvailability sourceAvailability,
    MakiDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<SuwayomiController> logger) : ControllerBase
{
    /// <param name="Password">
    /// Never returned. On PUT, null keeps the stored one for the same URL (and clears it for a new
    /// one), and an empty string clears it.
    /// </param>
    public record SuwayomiConnectionSettings(
        string? Url,
        string? AuthMode,
        string? Username,
        string? Password = null,
        bool PasswordSet = false);

    /// <param name="Error">Why Suwayomi could not be asked, localized; the stored list is left as it was.</param>
    public record SuwayomiRefreshResult(bool Reachable, string? Error, int Installed);

    public record SuwayomiSourceLanguage(string? ChapterLanguage);

    /// <summary>Serialises the snapshot's read-modify-write between a refresh and a language change.</summary>
    private static readonly SemaphoreSlim SnapshotGate = new(1, 1);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("settings/suwayomi")]
    public async Task<IActionResult> GetConnection(CancellationToken ct) => Ok(new SuwayomiConnectionSettings(
        await settings.GetAsync(SettingKeys.SuwayomiUrl, ct),
        AuthModeName(SuwayomiSettings.ParseAuthMode(await settings.GetAsync(SettingKeys.SuwayomiAuthMode, ct))),
        await settings.GetAsync(SettingKeys.SuwayomiUsername, ct),
        PasswordSet: !string.IsNullOrEmpty(await settings.GetAsync(SettingKeys.SuwayomiPassword, ct))));

    /// <summary>An empty URL turns the integration off; the exposed sources are kept for when it comes back.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("settings/suwayomi")]
    public async Task<IActionResult> SetConnection([FromBody] SuwayomiConnectionSettings request, CancellationToken ct)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            if (!SuwayomiSettings.TryParseBaseUri(request.Url, out var parsed))
            {
                return this.Fail(localizer, "error.settings.urlInvalid", new { service = "Suwayomi" });
            }

            url = parsed.GetLeftPart(UriPartial.Authority) + parsed.AbsolutePath.TrimEnd('/');
        }

        if (ParseAuthMode(request.AuthMode) is not { } mode)
        {
            return this.Fail(localizer, "error.suwayomi.authModeInvalid");
        }

        var username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
        if (mode != SuwayomiAuthMode.None && username is null)
        {
            return this.Fail(localizer, "error.suwayomi.usernameRequired");
        }

        // A stored password belongs to the server it was saved for. A new URL without a new password
        // clears it rather than sending the old one to another host, turning the integration off
        // included, or off-then-on with a different URL would carry it across.
        var previousUrl = await settings.GetAsync(SettingKeys.SuwayomiUrl, ct);
        // Switching to no authentication drops it too, since the form sends no password in that mode.
        var password = mode == SuwayomiAuthMode.None
            ? null
            : request.Password is not null
            ? request.Password.Length == 0 ? null : request.Password
            : !string.Equals(previousUrl, url, StringComparison.Ordinal)
                ? null
                : await settings.GetAsync(SettingKeys.SuwayomiPassword, ct);

        await settings.SetAsync(SettingKeys.SuwayomiUrl, url, ct);
        await settings.SetAsync(SettingKeys.SuwayomiAuthMode, AuthModeName(mode), ct);
        await settings.SetAsync(SettingKeys.SuwayomiUsername, username, ct);
        await settings.SetAsync(SettingKeys.SuwayomiPassword, password, ct);

        tokens.Clear();
        await sourceRegistry.ReloadAsync(ct);
        if (url is not null)
        {
            try
            {
                await RefreshSnapshotAsync(ct);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct))
            {
                // Saving still succeeds; Manage sources refreshes again when it opens.
                logger.LogInformation(ex, "Could not list Suwayomi's sources after saving the connection");
            }
        }

        return await GetConnection(ct);
    }

    /// <summary>
    /// Tests the posted values, falling back to the stored ones for anything left out (the password
    /// in particular, which the form never has).
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("settings/suwayomi/test")]
    public async Task<IActionResult> TestConnection([FromBody] SuwayomiConnectionSettings request, CancellationToken ct)
    {
        var stored = await suwayomiSettings.GetAsync(ct);
        var url = string.IsNullOrWhiteSpace(request.Url) ? stored?.BaseUri.ToString() : request.Url.Trim();
        if (url is null)
        {
            return this.Fail(localizer, "error.suwayomi.notConfigured");
        }

        if (!SuwayomiSettings.TryParseBaseUri(url, out var baseUri))
        {
            return this.Fail(localizer, "error.settings.urlInvalid", new { service = "Suwayomi" });
        }

        var mode = request.AuthMode is null ? stored?.AuthMode ?? SuwayomiAuthMode.None : ParseAuthMode(request.AuthMode);
        if (mode is null)
        {
            return this.Fail(localizer, "error.suwayomi.authModeInvalid");
        }

        // The stored password only goes to the server it was saved for, never to a URL being tried out.
        var sameServer = stored is not null && stored.BaseUri == baseUri;
        var connection = new SuwayomiConnection(
            baseUri, mode.Value,
            request.Username ?? stored?.Username,
            request.Password ?? (sameServer ? stored!.Password : null));
        try
        {
            var result = await tester.TestAsync(connection, ct);
            return Ok(new
            {
                success = true,
                version = result.About.Version,
                buildType = result.About.BuildType,
                sourceCount = result.SourceCount
            });
        }
        catch (SuwayomiVersionException ex)
        {
            return this.Fail(localizer, "error.suwayomi.versionUnsupported", new { version = ex.Version, minimum = ex.Minimum });
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            logger.LogInformation(ex, "Suwayomi connection test against {Url} failed", baseUri);
            return FailedToReach(ex);
        }
    }

    /// <summary>
    /// Reads Suwayomi's installed sources into the stored snapshot, so a newly installed extension
    /// shows up in Manage sources, switched off. Chapter languages already chosen are kept. A source
    /// Suwayomi no longer lists is dropped, unless series are linked to it: then it stays, marked
    /// missing, so those links keep a name and the health check can say what is wrong.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("settings/suwayomi/sources/refresh")]
    public async Task<IActionResult> RefreshSources(CancellationToken ct)
    {
        if (await suwayomiSettings.GetAsync(ct) is null)
        {
            return this.Fail(localizer, "error.suwayomi.notConfigured");
        }

        try
        {
            return Ok(new SuwayomiRefreshResult(true, null, await RefreshSnapshotAsync(ct)));
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            logger.LogInformation(ex, "Could not list Suwayomi's sources");
            return Ok(new SuwayomiRefreshResult(false, localizer.Get(UnreachableKey(ex)), 0));
        }
    }

    /// <summary>
    /// Sets the language a mixed-language source's chapters are filed under. Mihon chapters carry no
    /// language, so it is one per source; until it is set the source cannot be switched on.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("settings/suwayomi/sources/{id}")]
    public async Task<IActionResult> SetSourceLanguage(string id, [FromBody] SuwayomiSourceLanguage request, CancellationToken ct)
    {
        if (await suwayomiSettings.GetAsync(ct) is null)
        {
            return this.Fail(localizer, "error.suwayomi.notConfigured");
        }

        await SnapshotGate.WaitAsync(ct);
        try
        {
            var stored = (await StoredAsync(ct)).ToList();
            var index = stored.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return this.Fail(localizer, "error.suwayomi.unknownSource", new { id });
            }

            // For the rest the extension's own language is the truth, and overriding it would file
            // every chapter under a wrong one.
            var entry = stored[index];
            if (!SuwayomiStoredSource.IsMixedLanguage(entry.Lang))
            {
                return this.Fail(localizer, "error.suwayomi.chapterLanguageFixed", new { source = entry.DisplayName });
            }

            var language = request.ChapterLanguage?.Trim();
            if (string.IsNullOrEmpty(language))
            {
                return this.Fail(localizer, "error.suwayomi.chapterLanguageRequired", new { source = entry.DisplayName });
            }

            if (!LanguageCode().IsMatch(language))
            {
                return this.Fail(localizer, "error.suwayomi.chapterLanguageInvalid", new { language });
            }

            stored[index] = entry with { ChapterLanguage = language };
            await settings.SetAsync(SettingKeys.SuwayomiSources, SuwayomiSettings.Serialize(stored), ct);
        }
        finally
        {
            SnapshotGate.Release();
        }

        await sourceRegistry.ReloadAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Relays a Suwayomi thumbnail, which needs Suwayomi's credentials and usually lives on the LAN,
    /// so the cover proxy (public addresses only, no credentials) cannot fetch it. The caller picks
    /// two integers and nothing else, and only a manga of a source that is switched on is served:
    /// every installed source is registered, so without that any reader could browse the thumbnails
    /// of every extension installed.
    /// </summary>
    [HttpGet("suwayomi/thumbnail/{sourceId:long}/{mangaId:int}")]
    public async Task<IActionResult> Thumbnail(long sourceId, int mangaId, CancellationToken ct)
    {
        var id = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var name = SuwayomiSource.NamePrefix + id;
        if (sourceRegistry.Find(name) is not SuwayomiSource ||
            !await sourceAvailability.IsEnabledAsync(name, ct) ||
            await suwayomiSettings.GetAsync(ct) is not { } connection)
        {
            return NotFound();
        }

        try
        {
            var owner = cache.Owner(connection.BaseUri, mangaId);
            if (owner is null)
            {
                owner = await client.MangaSourceIdAsync(mangaId, ct);
                if (owner is not null)
                {
                    cache.SetOwner(connection.BaseUri, mangaId, owner);
                }
            }

            if (owner != id)
            {
                return NotFound();
            }

            using var response = await httpClientFactory.CreateClient(SuwayomiClient.PagesHttpClientName).GetAsync(
                connection.Resolve($"/api/v1/manga/{mangaId}/thumbnail"), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return this.BadGateway(localizer, "error.search.coverUnavailable");
            }

            var bytes = await SearchController.ReadCappedAsync(response.Content, SearchController.MaxCoverBytes, ct);
            if (bytes is null)
            {
                return this.BadGateway(localizer, "error.search.coverUnavailable");
            }

            // Same rules as the cover proxy: the bytes decide the type, anything but a raster is
            // refused, and the per-response CSP keeps even a crafted image inert.
            if (ImageValidator.SniffMediaType(bytes) is not { } mediaType)
            {
                return this.BadGateway(localizer, "error.search.notAnImage");
            }

            Response.Headers.CacheControl = "private,max-age=86400";
            Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return File(bytes, mediaType);
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            logger.LogDebug(ex, "Suwayomi thumbnail {MangaId} could not be fetched", mangaId);
            return this.BadGateway(localizer, "error.search.coverUnavailable");
        }
    }

    /// <returns>How many sources Suwayomi listed.</returns>
    private async Task<int> RefreshSnapshotAsync(CancellationToken ct)
    {
        var live = (await client.ListSourcesAsync(ct)).DistinctBy(s => s.Id).ToList();
        var linked = (await db.SourceMappings.IgnoreQueryFilters()
                .Where(m => m.SourceName.StartsWith(SuwayomiSource.NamePrefix))
                .Select(m => m.SourceName)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        await SnapshotGate.WaitAsync(ct);
        try
        {
            var before = await settings.GetAsync(SettingKeys.SuwayomiSources, ct);
            var stored = (await StoredAsync(ct)).DistinctBy(e => e.Id).ToDictionary(e => e.Id, StringComparer.Ordinal);
            var next = live.Select(source => new SuwayomiStoredSource(
                    source.Id, source.Name, source.DisplayName, source.Lang,
                    SuwayomiStoredSource.IsMixedLanguage(source.Lang) ? stored.GetValueOrDefault(source.Id)?.ChapterLanguage : null,
                    source.ContentWarning, source.IsNsfw))
                .ToList();
            var liveIds = live.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            next.AddRange(stored.Values
                .Where(e => !liveIds.Contains(e.Id) && linked.Contains(SuwayomiSource.NamePrefix + e.Id))
                .Select(e => e with { Missing = true }));

            var after = next.Count == 0
                ? null
                : SuwayomiSettings.Serialize(next
                    .OrderBy(e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.Name : e.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Id, StringComparer.Ordinal));
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                return live.Count;
            }

            await settings.SetAsync(SettingKeys.SuwayomiSources, after, ct);
        }
        finally
        {
            SnapshotGate.Release();
        }

        await sourceRegistry.ReloadAsync(ct);
        return live.Count;
    }

    private async Task<IReadOnlyList<SuwayomiStoredSource>> StoredAsync(CancellationToken ct)
    {
        try
        {
            return await suwayomiSettings.StoredAsync(ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(ex, "The stored Suwayomi source list is not valid JSON; treating it as empty");
            return [];
        }
    }

    private static SuwayomiAuthMode? ParseAuthMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => SuwayomiAuthMode.None,
        "basic" => SuwayomiAuthMode.Basic,
        "uilogin" => SuwayomiAuthMode.UiLogin,
        _ => null
    };

    private static string AuthModeName(SuwayomiAuthMode mode) => mode switch
    {
        SuwayomiAuthMode.Basic => "basic",
        SuwayomiAuthMode.UiLogin => "uilogin",
        _ => "none"
    };

    /// <summary>Failures that mean "could not get a usable answer from Suwayomi", as opposed to bugs.</summary>
    private static bool IsUnreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or SourceErrorException or InvalidOperationException or RateLimitException ||
        (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested);

    private static string UnreachableKey(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "error.suwayomi.loginFailed",
        HttpRequestException { StatusCode: { } status } when (int)status is >= 300 and < 400 => "error.suwayomi.redirected",
        _ => "error.suwayomi.noResponse"
    };

    private IActionResult FailedToReach(Exception ex) => this.BadGateway(localizer, UnreachableKey(ex));

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageCode();
}
