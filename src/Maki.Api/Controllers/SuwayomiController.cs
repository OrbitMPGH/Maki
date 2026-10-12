using System.Net;
using System.Text.RegularExpressions;
using Maki.Api.Auth;
using Maki.Api.Localization;
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
/// The optional Suwayomi integration: its connection, which of its sources Maki uses, and a relay
/// for its thumbnails. Kept out of <c>SettingsController</c> so that controller's constructor does
/// not grow for an add-on. The settings actions are admin-only one by one, the same rule
/// <c>SettingsController</c> follows, because <c>GET settings/suwayomi/sources</c> reaches the server
/// with the stored credentials. The thumbnail relay is for any signed-in reader, like the cover proxy.
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
    MakiDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<SuwayomiController> logger) : ControllerBase
{
    /// <param name="Password">Never returned. On PUT, null leaves the stored one alone and an empty string clears it.</param>
    public record SuwayomiConnectionSettings(
        string? Url,
        string? AuthMode,
        string? Username,
        string? Password = null,
        bool PasswordSet = false);

    public record SuwayomiSourceChoice(string Id, string? ChapterLanguage = null);

    public record SuwayomiSourcesRequest(List<SuwayomiSourceChoice> Sources);

    /// <param name="Installed">Null when Suwayomi could not be asked.</param>
    /// <param name="Mappings">Series linked to it, so removing it can say what stops syncing.</param>
    public record SuwayomiSourceRow(
        string Id,
        string SourceName,
        string Name,
        string DisplayName,
        string Lang,
        string? ContentWarning,
        bool IsNsfw,
        bool MixedLanguage,
        bool? Installed,
        bool Exposed,
        string? ChapterLanguage,
        int Mappings);

    /// <param name="Error">Why Suwayomi could not be asked, localized; the stored sources are still listed.</param>
    public record SuwayomiSourcesResponse(bool Reachable, string? Error, IReadOnlyList<SuwayomiSourceRow> Sources);

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

        await settings.SetAsync(SettingKeys.SuwayomiUrl, url, ct);
        await settings.SetAsync(SettingKeys.SuwayomiAuthMode, AuthModeName(mode), ct);
        await settings.SetAsync(SettingKeys.SuwayomiUsername, username, ct);
        if (request.Password is not null)
        {
            await settings.SetAsync(SettingKeys.SuwayomiPassword, request.Password.Length == 0 ? null : request.Password, ct);
        }

        tokens.Clear();
        await sourceRegistry.ReloadAsync(ct);
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

    /// <summary>Every installed source merged with the stored selection, including selected ones Suwayomi no longer has.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("settings/suwayomi/sources")]
    public async Task<IActionResult> GetSources(CancellationToken ct)
    {
        if (await suwayomiSettings.GetAsync(ct) is null)
        {
            return this.Fail(localizer, "error.suwayomi.notConfigured");
        }

        var exposed = (await StoredAsync(ct)).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var mappings = await db.SourceMappings.IgnoreQueryFilters()
            .Where(m => m.SourceName.StartsWith(SuwayomiSource.NamePrefix))
            .GroupBy(m => m.SourceName)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        IReadOnlyList<SuwayomiSourceInfo>? live = null;
        string? error = null;
        try
        {
            live = await client.ListSourcesAsync(ct);
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            logger.LogInformation(ex, "Could not list Suwayomi's sources");
            error = localizer.Get(UnreachableKey(ex));
        }

        var rows = new List<SuwayomiSourceRow>();
        foreach (var source in live ?? [])
        {
            exposed.TryGetValue(source.Id, out var chosen);
            rows.Add(Row(source.Id, source.Name, source.DisplayName, source.Lang, source.ContentWarning, source.IsNsfw,
                installed: true, chosen, mappings));
        }

        var liveIds = (live ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var chosen in exposed.Values.Where(e => !liveIds.Contains(e.Id)))
        {
            rows.Add(Row(chosen.Id, chosen.Name, chosen.DisplayName, chosen.Lang, chosen.ContentWarning, chosen.IsNsfw,
                installed: live is null ? null : false, chosen, mappings));
        }

        return Ok(new SuwayomiSourcesResponse(
            live is not null, error,
            [.. rows.OrderBy(r => r.Lang, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)]));
    }

    /// <summary>
    /// Replaces the selection. Names, languages and content warnings are taken from Suwayomi, not the
    /// request, so the stored snapshot says what the server says; a source Suwayomi no longer lists
    /// keeps the snapshot it already had, and is accepted only if it was already selected.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("settings/suwayomi/sources")]
    public async Task<IActionResult> SetSources([FromBody] SuwayomiSourcesRequest request, CancellationToken ct)
    {
        if (await suwayomiSettings.GetAsync(ct) is null)
        {
            return this.Fail(localizer, "error.suwayomi.notConfigured");
        }

        var stored = (await StoredAsync(ct)).ToDictionary(e => e.Id, StringComparer.Ordinal);
        Dictionary<string, SuwayomiSourceInfo> live;
        Exception? unreachable = null;
        try
        {
            live = (await client.ListSourcesAsync(ct)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            // Unreachable still allows dropping sources, or keeping ones already chosen.
            logger.LogInformation(ex, "Could not list Suwayomi's sources while saving the selection");
            live = [];
            unreachable = ex;
        }

        var next = new List<SuwayomiExposedSource>();
        foreach (var choice in (request.Sources ?? []).DistinctBy(c => c.Id))
        {
            SuwayomiExposedSource entry;
            if (live.TryGetValue(choice.Id, out var source))
            {
                entry = new SuwayomiExposedSource(
                    source.Id, source.Name, source.DisplayName, source.Lang, null, source.ContentWarning, source.IsNsfw);
            }
            else if (stored.TryGetValue(choice.Id, out var kept))
            {
                entry = kept with { ChapterLanguage = null };
            }
            else
            {
                return unreachable is not null
                    ? FailedToReach(unreachable)
                    : this.Fail(localizer, "error.suwayomi.unknownSource", new { id = choice.Id });
            }

            // Only a mixed-language source takes a chapter language: for the rest, the extension's own
            // language is the truth, and overriding it would file every chapter under a wrong one.
            if (SuwayomiExposedSource.IsMixedLanguage(entry.Lang))
            {
                var language = choice.ChapterLanguage?.Trim();
                if (string.IsNullOrEmpty(language))
                {
                    return this.Fail(localizer, "error.suwayomi.chapterLanguageRequired", new { source = entry.DisplayName });
                }

                if (!LanguageCode().IsMatch(language))
                {
                    return this.Fail(localizer, "error.suwayomi.chapterLanguageInvalid", new { language });
                }

                entry = entry with { ChapterLanguage = language };
            }

            next.Add(entry);
        }

        await settings.SetAsync(SettingKeys.SuwayomiSources, next.Count == 0 ? null : SuwayomiSettings.Serialize(next), ct);
        await sourceRegistry.ReloadAsync(ct);
        return await GetSources(ct);
    }

    /// <summary>
    /// Relays a Suwayomi thumbnail, which needs Suwayomi's credentials and usually lives on the LAN,
    /// so the cover proxy (public addresses only, no credentials) cannot fetch it. The caller picks
    /// two integers and nothing else, and only a manga of a source the admin exposed is served:
    /// without that, any reader could browse the thumbnails of every extension installed.
    /// </summary>
    [HttpGet("suwayomi/thumbnail/{sourceId:long}/{mangaId:int}")]
    public async Task<IActionResult> Thumbnail(long sourceId, int mangaId, CancellationToken ct)
    {
        var id = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (sourceRegistry.Find(SuwayomiSource.NamePrefix + id) is not SuwayomiSource ||
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

    private async Task<IReadOnlyList<SuwayomiExposedSource>> StoredAsync(CancellationToken ct)
    {
        try
        {
            return await suwayomiSettings.ExposedAsync(ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(ex, "The stored Suwayomi source list is not valid JSON; treating it as empty");
            return [];
        }
    }

    private static SuwayomiSourceRow Row(
        string id, string name, string displayName, string lang, string? contentWarning, bool isNsfw,
        bool? installed, SuwayomiExposedSource? chosen, Dictionary<string, int> mappings)
    {
        var sourceName = SuwayomiSource.NamePrefix + id;
        return new SuwayomiSourceRow(
            id, sourceName, name, displayName, lang, contentWarning, isNsfw,
            SuwayomiExposedSource.IsMixedLanguage(lang), installed, chosen is not null, chosen?.ChapterLanguage,
            mappings.GetValueOrDefault(sourceName));
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
