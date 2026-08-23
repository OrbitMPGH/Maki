using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
// Admin per *action*, not per class, because a handful of reads here are things the app needs before
// it knows whether the user is an admin: which page "/" resolves to, whether Discover has its local
// database, the reader's display defaults. Those are listed explicitly below and require only a
// signed-in user; every other endpoint — and every write without exception — is admin-only.
//
// Reads matter as much as writes on the rest: these endpoints return the Prowlarr and Kavita API keys,
// the qBittorrent password and the tracker client secrets, all stored in plaintext by design.
//
// "reader", "ui", "discover", "opds" and the per-tracker halves of "scrobble" are per-user, stored in
// UserSettings and read through the scoped IUserSettings — so their writes need no admin policy: a
// caller can only ever change their own. Everything else here describes the deployment (ports, paths,
// Prowlarr/qBittorrent/Kavita connections, source priority, updates) or an app registration shared by
// everyone (a tracker's client id and secret), and stays admin-only.
public class SettingsController(
    SettingsService settings,
    FlareSolverrClient flareSolverr,
    Maki.Core.Indexers.ProwlarrClient prowlarr,
    Maki.Core.Download.QBittorrentClient qbittorrent,
    Maki.Core.Kavita.KavitaClient kavita,
    ConfigFileProvider configFile,
    SourceRegistry sourceRegistry,
    SourceAvailability sourceAvailability,
    MangaBakaDumpService mangaBakaDump,
    EmbeddingModelStore embeddingModel,
    EmbeddingStore embeddingStore,
    EmbeddingIndexStatus embeddingStatus,
    SeriesEmbeddingIndexer embeddingIndexer,
    EmbeddingOptions embeddingOptions,
    PrebuiltIndexInstaller prebuiltIndex,
    EmbeddingModelSwitcher modelSwitcher,
    Maki.Data.MakiDbContext db,
    UpdateCheckService updateCheck,
    ICurrentUser currentUser,
    IUserSettings userSettings,
    KavitaUserResolver kavitaUser,
    ISchedulerFactory schedulerFactory) : ControllerBase
{
    public record FlareSolverrSettings(string? Url);
    public record ProwlarrSettings(string? Url, string? ApiKey);
    public record QBittorrentSettings(
        string? Url, string? Username, string? Password, string? Category, string? PathMapFrom, string? PathMapTo);
    public record MetadataSettings(bool UseLocalDb);
    public record MetadataSettingsResponse(bool UseLocalDb, bool DumpPresent, long? DumpSizeBytes, DateTime? DumpRefreshedAt);
    public record MonitoringSettings(bool UnmonitorSpecials);
    /// <param name="IncognitoByRating">
    /// Content rating → <see cref="IncognitoMode"/> name, the default a newly added series of that
    /// rating starts at. Null on a write leaves the stored rules alone, so a caller that predates
    /// the field (the setup wizard's own two switches) can't blank them out.
    /// </param>
    public record LibrarySettings(
        bool WriteComicInfo,
        string FolderNamingMode,
        Dictionary<string, string>? IncognitoByRating = null);
    public record SetupStatus(bool Completed);
    /// <param name="ItemTimeoutMinutes">
    /// Wall-clock cap on one chapter download before the worker abandons it. 0 means no cap.
    /// See <see cref="SettingKeys.DownloadItemTimeoutMinutes"/>.
    /// </param>
    public record DownloadSettings(
        int ConcurrentChapters, bool RetryEnabled, int RetryMaxAttempts,
        int SmartDownloadChaptersLeft, int SmartDownloadChapters, int ItemTimeoutMinutes);
    public record BackupSettings(int Retention);
    public record UpdateSettings(bool CheckForUpdates);
    public record DiscoverSettings(string MaxContentRating);
    /// <param name="UserId">
    /// Which Maki user Kavita's reading belongs to. Null means "the lowest-numbered admin", which is
    /// what a single-user install wants and needs no configuration. See
    /// <see cref="SettingKeys.KavitaUserId"/> for why this has to be exactly one user.
    /// </param>
    /// <param name="ResolvedUserId">
    /// Read-only: who the null default actually resolved to, so the UI can say whose reading is being
    /// tracked instead of showing an empty select.
    /// </param>
    public record KavitaSettings(
        string? Url, string? ApiKey, string? PathMapFrom, string? PathMapTo,
        int? UserId = null, int? ResolvedUserId = null);
    public record ReaderSettings(
        Maki.Core.Reading.ReaderPrefsSpec Defaults, bool PushToKavita, int? KavitaUserId = null);
    public record UiSettings(string StartPage, HomeLayoutSpec HomeLayout);
    public record OpdsSettings(bool Enabled, bool TrackProgress);

    public record SecuritySettings(
        bool RequireHttps,
        string TrustedProxies,
        int LockoutMaxAttempts,
        int LockoutMinutes,
        int SessionDays);

    /// <param name="ClientSecret">
    /// Returned in plaintext, like every other secret this controller serves — which is why the
    /// whole endpoint is admin-only. See the settings-secrets note in CLAUDE.md.
    /// </param>
    /// <param name="RedirectPath">
    /// Read-only. The path to register with the provider as this client's redirect URI, shown so an
    /// admin does not have to find it in the documentation.
    /// </param>
    /// <param name="BreakGlassActive">
    /// Read-only. <c>MAKI_ALLOW_LOCAL_LOGIN</c> is set in the environment, so <see cref="OidcOnly"/>
    /// is being ignored. Surfaced because otherwise the setting reads as on while doing nothing.
    /// </param>
    public record OidcSettings(
        bool Enabled,
        string Authority,
        string ClientId,
        string ClientSecret,
        string Scopes,
        string DisplayName,
        bool OidcOnly,
        bool AutoProvision,
        string UsernameClaim,
        string AdminClaim,
        string PermissionClaim,
        string? RedirectPath = null,
        bool BreakGlassActive = false);

    /// <param name="HasToken">Whether a live OPDS token exists for this user.</param>
    /// <param name="TokenPrefix">First few characters of the token, for identifying it. Not usable as a credential.</param>
    /// <param name="FeedUrl">The path to paste into a reading app, relative so it works whatever host
    /// or reverse proxy the instance is reached through. Non-null <b>only</b> on the response that
    /// generated the token — nothing stores the token itself, so it cannot be shown again.</param>
    public record OpdsSettingsResponse(
        bool Enabled, bool TrackProgress, bool HasToken, string? TokenPrefix, string? FeedUrl);

    /// <summary>
    /// Blank clears the setting; anything else must be an absolute http(s) URL. Rejecting garbage
    /// on save means the error names the field the user just typed in, instead of surfacing later
    /// as a confusing connection failure when they click Test.
    /// </summary>
    private static bool IsValidServiceUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private static string UrlError(string service) =>
        $"{service} URL must be a full http:// or https:// address (e.g. http://localhost:8080), or blank to clear it";

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("monitoring")]
    public async Task<IActionResult> GetMonitoring(CancellationToken ct) => Ok(new MonitoringSettings(
        await settings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("monitoring")]
    public async Task<IActionResult> SetMonitoring([FromBody] MonitoringSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.MonitoringUnmonitorSpecials, request.UnmonitorSpecials ? "true" : "false", ct);
        return Ok(request);
    }

    /// <summary>
    /// Built-in reader defaults. A series can override the whole spec — see
    /// <c>PUT /series/{id}/readerprefs</c>; the reader's manifest serves the merged result.
    /// </summary>
    [HttpGet("reader")]
    public async Task<IActionResult> GetReader(CancellationToken ct)
    {
        var stored = await userSettings.GetManyAsync(
            [SettingKeys.ReaderPrefs, SettingKeys.ReaderPushToKavita], ct);
        return Ok(new ReaderSettings(
            Maki.Core.Reading.ReaderPrefsSpec.Parse(stored.GetValueOrDefault(SettingKeys.ReaderPrefs)),
            stored.GetValueOrDefault(SettingKeys.ReaderPushToKavita) == "true",
            KavitaUserId: await kavitaUser.ResolveAsync(ct)));
    }

    /// <summary>
    /// The caller's own reader defaults. No admin policy: these land in their <c>UserSettings</c> rows.
    /// <para>
    /// <c>KavitaUserId</c> on the response is read-only here — it is an instance setting (Kavita is one
    /// external account) and is set through <c>PUT settings/kavita</c>. It rides along because the
    /// reader card is where "push my reads to Kavita" lives, and that toggle only does anything for the
    /// bound user.
    /// </para>
    /// </summary>
    [HttpPut("reader")]
    public async Task<IActionResult> SetReader([FromBody] ReaderSettings request, CancellationToken ct)
    {
        var defaults = (request.Defaults ?? new Maki.Core.Reading.ReaderPrefsSpec()).Sanitized();
        await userSettings.SetAsync(SettingKeys.ReaderPrefs,
            Maki.Core.Reading.ReaderPrefsSpec.Serialize(defaults), ct);
        await userSettings.SetAsync(SettingKeys.ReaderPushToKavita, request.PushToKavita ? "true" : "false", ct);
        return Ok(new ReaderSettings(defaults, request.PushToKavita, await kavitaUser.ResolveAsync(ct)));
    }

    /// <summary>
    /// The OPDS catalogue. Off by default — see <see cref="SettingKeys.OpdsEnabled"/>.
    /// <para>
    /// The feed URL is no longer readable after the fact. The token is a
    /// <c>UserApiKey</c> row and only its SHA-256 digest is stored, so this reports whether one exists
    /// and its display prefix; the full URL is shown exactly once, when it is generated. That is the
    /// price of not keeping a URL-borne credential in the database in plaintext, and it is the same
    /// deal every API key in the app now gets.
    /// </para>
    /// </summary>
    // Needs UseOpds, not admin: the catalogue, its switches and its token are all this user's own, and
    // a reader-only account being able to point a reading app at its own library is the point.
    [Authorize(Policy = Policies.UseOpds)]
    [HttpGet("opds")]
    public async Task<IActionResult> GetOpds(CancellationToken ct)
    {
        var existing = await CurrentOpdsKeyAsync(ct);
        var stored = await userSettings.GetManyAsync(
            [SettingKeys.OpdsEnabled, SettingKeys.OpdsTrackProgress], ct);
        return Ok(new OpdsSettingsResponse(
            stored.GetValueOrDefault(SettingKeys.OpdsEnabled) == "true",
            stored.GetValueOrDefault(SettingKeys.OpdsTrackProgress) != "false",
            existing is not null,
            existing?.Prefix,
            FeedUrl: null));
    }

    /// <summary>
    /// Enabling mints a token if this user has none. Disabling deliberately keeps the existing one, so
    /// switching OPDS off and on again doesn't silently break every reader already configured with it
    /// — throwing readers off is what <c>opds/token</c> is for.
    /// </summary>
    [Authorize(Policy = Policies.UseOpds)]
    [HttpPut("opds")]
    public async Task<IActionResult> SetOpds([FromBody] OpdsSettings request, CancellationToken ct)
    {
        await userSettings.SetAsync(SettingKeys.OpdsEnabled, request.Enabled ? "true" : "false", ct);
        await userSettings.SetAsync(
            SettingKeys.OpdsTrackProgress, request.TrackProgress ? "true" : "false", ct);

        var existing = await CurrentOpdsKeyAsync(ct);
        if (request.Enabled && existing is null)
        {
            var (prefix, feedUrl) = await MintOpdsKeyAsync(ct);
            return Ok(new OpdsSettingsResponse(true, request.TrackProgress, true, prefix, feedUrl));
        }

        return Ok(new OpdsSettingsResponse(
            request.Enabled, request.TrackProgress, existing is not null, existing?.Prefix, FeedUrl: null));
    }

    /// <summary>Mints a fresh token and revokes the previous one, invalidating every feed URL already handed out.</summary>
    [Authorize(Policy = Policies.UseOpds)]
    [HttpPost("opds/token")]
    public async Task<IActionResult> RotateOpdsToken(CancellationToken ct)
    {
        var (prefix, feedUrl) = await MintOpdsKeyAsync(ct);
        var stored = await userSettings.GetManyAsync(
            [SettingKeys.OpdsEnabled, SettingKeys.OpdsTrackProgress], ct);
        return Ok(new OpdsSettingsResponse(
            stored.GetValueOrDefault(SettingKeys.OpdsEnabled) == "true",
            stored.GetValueOrDefault(SettingKeys.OpdsTrackProgress) != "false",
            true,
            prefix,
            feedUrl));
    }

    private Task<UserApiKey?> CurrentOpdsKeyAsync(CancellationToken ct) =>
        db.UserApiKeys
            .AsNoTracking()
            .Where(k => k.UserId == currentUser.UserId
                        && k.Scope == UserApiKeyScope.Opds
                        && k.RevokedAt == null)
            .OrderByDescending(k => k.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Revokes any live OPDS token for this user and issues one new one. Revoking rather than deleting
    /// keeps the rotation visible in the account UI and in the audit trail.
    /// </summary>
    private async Task<(string Prefix, string FeedUrl)> MintOpdsKeyAsync(CancellationToken ct)
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;

        await db.UserApiKeys
            .Where(k => k.UserId == currentUser.UserId
                        && k.Scope == UserApiKeyScope.Opds
                        && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), ct);

        var secret = ApiKeyCrypto.Generate();
        db.UserApiKeys.Add(new UserApiKey
        {
            UserId = currentUser.UserId,
            Name = "OPDS feed",
            KeyHash = ApiKeyCrypto.Hash(secret),
            Prefix = ApiKeyCrypto.Prefix(secret),
            Scope = UserApiKeyScope.Opds,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        // Root-relative on purpose. Building an absolute URL from Request.Scheme/Host hands out an
        // http:// link through any TLS-terminating proxy that doesn't rewrite it.
        return (ApiKeyCrypto.Prefix(secret), $"/api/v1/opds/{secret}");
    }

    /// <summary>
    /// Which page "/" resolves to, and how Home is laid out. An unrecognised stored start page
    /// reads as the default rather than erroring — a setting written by a newer build shouldn't
    /// leave the UI unable to load a page — and the layout blob is merged against this build's
    /// section list on the way out (see <see cref="HomeLayoutSpec.Merge"/>).
    /// </summary>
    [HttpGet("ui")]
    public async Task<IActionResult> GetUi(CancellationToken ct)
    {
        var rows = await userSettings.GetManyAsync(
            [SettingKeys.UiStartPage, SettingKeys.UiHomeSections], ct);
        var stored = rows.GetValueOrDefault(SettingKeys.UiStartPage);
        var layout = HomeLayoutSpec.Parse(rows.GetValueOrDefault(SettingKeys.UiHomeSections));
        return Ok(new UiSettings(StartPage.IsValid(stored) ? stored! : StartPage.Default, layout));
    }

    /// <summary>Which page this user lands on, and how their Home is laid out. Theirs alone.</summary>
    [HttpPut("ui")]
    public async Task<IActionResult> SetUi([FromBody] UiSettings request, CancellationToken ct)
    {
        if (!StartPage.IsValid(request.StartPage))
        {
            return BadRequest(new { error = $"Unknown start page: {request.StartPage}" });
        }

        // Turning Home off while it is the start page would leave "/" pointing at a page the client
        // then bounces away from. The client already falls back for exactly this, but storing the
        // contradiction means the setting silently disagrees with what the user sees; resolve it here.
        var layout = (request.HomeLayout ?? HomeLayoutSpec.Default).Merge();
        var startPage = !layout.Enabled && request.StartPage == StartPage.Home
            ? StartPage.Library
            : request.StartPage;

        await userSettings.SetAsync(SettingKeys.UiStartPage, startPage, ct);
        await userSettings.SetAsync(SettingKeys.UiHomeSections, HomeLayoutSpec.Serialize(layout), ct);
        return Ok(new UiSettings(startPage, layout));
    }

    [HttpGet("library")]
    public async Task<IActionResult> GetLibrary(CancellationToken ct)
    {
        var mode = await settings.GetAsync(SettingKeys.LibraryFolderNamingMode, ct);
        var incognito = IncognitoRatingRules.Parse(
            await settings.GetAsync(SettingKeys.LibraryIncognitoByRating, ct));

        // Every rating is spelled out, including the ones set to Off: the client renders one control
        // per rating either way, and a response that only carried the non-Off entries would make the
        // stored-but-off and never-configured cases indistinguishable on the way back in.
        return Ok(new LibrarySettings(
            await settings.GetAsync(SettingKeys.LibraryWriteComicInfo, ct) != "false",
            Maki.Core.Naming.FolderNamingMode.IsValid(mode) ? mode! : Maki.Core.Naming.FolderNamingMode.Default,
            ContentRating.All.ToDictionary(
                r => r,
                r => IncognitoRatingRules.Resolve(incognito, r).ToString())));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("library")]
    public async Task<IActionResult> SetLibrary([FromBody] LibrarySettings request, CancellationToken ct)
    {
        if (!Maki.Core.Naming.FolderNamingMode.IsValid(request.FolderNamingMode))
        {
            return BadRequest(new { error = $"Unknown folder naming mode: {request.FolderNamingMode}" });
        }

        if (request.IncognitoByRating is { } rules)
        {
            var parsed = new Dictionary<string, IncognitoMode>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rating, mode) in rules)
            {
                if (!ContentRating.IsValid(rating))
                {
                    return BadRequest(new { error = $"Unknown content rating: {rating}" });
                }

                if (!Enum.TryParse<IncognitoMode>(mode, true, out var parsedMode))
                {
                    return BadRequest(new { error = $"Unknown incognito mode: {mode}" });
                }

                parsed[rating] = parsedMode;
            }

            await settings.SetAsync(
                SettingKeys.LibraryIncognitoByRating, IncognitoRatingRules.Serialize(parsed), ct);
        }

        await settings.SetAsync(SettingKeys.LibraryWriteComicInfo, request.WriteComicInfo ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.LibraryFolderNamingMode, request.FolderNamingMode, ct);
        return Ok(request);
    }

    /// <summary>
    /// The first-run guide shows only when this reports not-completed. The flag is tri-state:
    /// "true"/"false" are explicit (finishing/skipping vs. the "Run setup guide" button re-opening
    /// it), and unset falls back to "has a root folder" — an existing user upgrading into this
    /// feature already has one and shouldn't be nagged, a fresh install doesn't and gets the guide.
    /// </summary>
    [HttpGet("setup")]
    public async Task<IActionResult> GetSetup(CancellationToken ct)
    {
        var stored = await settings.GetAsync(SettingKeys.SetupCompleted, ct);
        if (stored is not null)
        {
            return Ok(new SetupStatus(stored == "true"));
        }

        var hasRootFolder = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .AnyAsync(db.RootFolders, ct);
        return Ok(new SetupStatus(hasRootFolder));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("setup")]
    public async Task<IActionResult> SetSetup([FromBody] SetupStatus request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.SetupCompleted, request.Completed ? "true" : "false", ct);
        return Ok(request);
    }

    /// <summary>
    /// The caller's own content-rating ceiling. It is a column on their account rather than a setting,
    /// so this reads back what <c>ICurrentUser</c> already loaded for the request — no query.
    /// </summary>
    [HttpGet("discover")]
    public IActionResult GetDiscover() =>
        Ok(new DiscoverSettings(
            ContentRating.IsValid(currentUser.MaxContentRating)
                ? currentUser.MaxContentRating
                : ContentRating.Default));

    /// <summary>
    /// Raises or lowers the caller's own ceiling, gated on <c>ChangeContentRating</c> — the point of
    /// that permission is that an admin can hand out an account which cannot lift its own filter.
    /// An admin edits anybody's through <c>PUT users/{id}</c>.
    /// </summary>
    [Authorize(Policy = Policies.ChangeContentRating)]
    [HttpPut("discover")]
    public async Task<IActionResult> SetDiscover([FromBody] DiscoverSettings request, CancellationToken ct)
    {
        if (!ContentRating.IsValid(request.MaxContentRating))
        {
            return BadRequest(new { error = $"Unknown content rating: {request.MaxContentRating}" });
        }

        await db.Users
            .Where(u => u.Id == currentUser.UserId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.MaxContentRating, request.MaxContentRating), ct);
        return Ok(new DiscoverSettings(request.MaxContentRating));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("download")]
    public async Task<IActionResult> GetDownload(CancellationToken ct) => Ok(new DownloadSettings(
        int.TryParse(await settings.GetAsync(SettingKeys.DownloadConcurrentChapters, ct), out var n) ? n : 2,
        await settings.GetAsync(SettingKeys.DownloadRetryEnabled, ct) != "false",
        int.TryParse(await settings.GetAsync(SettingKeys.DownloadRetryMaxAttempts, ct), out var r) ? r : 5,
        int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersLeft, ct), out var l) ? l : 5,
        int.TryParse(await settings.GetAsync(SettingKeys.SmartDownloadChaptersCount, ct), out var c) ? c : 10,
        int.TryParse(await settings.GetAsync(SettingKeys.DownloadItemTimeoutMinutes, ct), out var t) ? t : 120));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("download")]
    public async Task<IActionResult> SetDownload([FromBody] DownloadSettings request, CancellationToken ct)
    {
        if (request.ConcurrentChapters is < 1 or > 8)
        {
            return BadRequest(new { error = "Concurrent chapter downloads must be between 1 and 8" });
        }

        if (request.RetryMaxAttempts is < 1 or > 20)
        {
            return BadRequest(new { error = "Retry attempts must be between 1 and 20" });
        }

        // 0 is "no cap", the escape hatch for a source slower than any number worth defaulting to.
        // The lower bound is not 1: a cap under about ten minutes would abandon perfectly healthy
        // downloads on a rate-limited source, which looks exactly like the stall it exists to end.
        if (request.ItemTimeoutMinutes != 0 && request.ItemTimeoutMinutes is < 10 or > 1440)
        {
            return BadRequest(new { error = "Download timeout must be 0 (no limit) or between 10 and 1440 minutes" });
        }

        await settings.SetAsync(
            SettingKeys.DownloadConcurrentChapters,
            request.ConcurrentChapters.ToString(CultureInfo.InvariantCulture),
            ct);
        await settings.SetAsync(SettingKeys.DownloadRetryEnabled, request.RetryEnabled ? "true" : "false", ct);
        await settings.SetAsync(
            SettingKeys.DownloadRetryMaxAttempts,
            request.RetryMaxAttempts.ToString(CultureInfo.InvariantCulture),
            ct);
        await settings.SetAsync(SettingKeys.SmartDownloadChaptersLeft,
            request.SmartDownloadChaptersLeft.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.SmartDownloadChaptersCount,
            request.SmartDownloadChapters.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.DownloadItemTimeoutMinutes,
            request.ItemTimeoutMinutes.ToString(CultureInfo.InvariantCulture), ct);
        return Ok(request);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("backup")]
    public async Task<IActionResult> GetBackup(CancellationToken ct) => Ok(new BackupSettings(
        int.TryParse(await settings.GetAsync(SettingKeys.BackupRetention, ct), out var n) ? n : 5));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("backup")]
    public async Task<IActionResult> SetBackup([FromBody] BackupSettings request, CancellationToken ct)
    {
        if (request.Retention is < 1 or > 50)
        {
            return BadRequest(new { error = "Backups to keep must be between 1 and 50" });
        }

        await settings.SetAsync(
            SettingKeys.BackupRetention,
            request.Retention.ToString(CultureInfo.InvariantCulture),
            ct);
        return Ok(request);
    }

    /// <summary>
    /// <paramref name="Order"/> is every registered source, most preferred first.
    /// <paramref name="Disabled"/> is the subset switched off globally — it stays *inside* the
    /// order rather than being removed from it, so a source keeps its rank across an off/on cycle.
    /// </summary>
    public record SourcePrioritySettings(List<string> Order, List<string> Disabled);

    /// <summary>
    /// Full list of registered source names, ordered by preference: sources named in the stored
    /// priority setting come first (in that order), then any remaining registered sources.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("sources/priority")]
    public async Task<IActionResult> GetSourcePriority(CancellationToken ct)
    {
        var ordered = SourceMatchService.OrderSources(
            sourceRegistry.All, await settings.GetAsync(SettingKeys.SourcePriorityOrder, ct));
        return Ok(new SourcePrioritySettings(
            ordered.Select(s => s.Name).ToList(),
            await sourceAvailability.DisabledAsync(ct)));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("sources/priority")]
    public async Task<IActionResult> SetSourcePriority([FromBody] SourcePrioritySettings request, CancellationToken ct)
    {
        var unknown = request.Order.Concat(request.Disabled)
            .Where(name => sourceRegistry.Find(name) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0)
        {
            return BadRequest(new { error = $"Unknown source(s): {string.Join(", ", unknown)}" });
        }

        // Switching a source off writes one setting and nothing else — per-series
        // SourceMapping.Enabled flags are deliberately left alone so that turning it back
        // on restores the layout the user had rather than a blanket "everything on".
        await settings.SetAsync(SettingKeys.SourcePriorityOrder, string.Join(',', request.Order), ct);
        await settings.SetAsync(SettingKeys.SourcesDisabled, string.Join(',', request.Disabled), ct);
        return await GetSourcePriority(ct);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("prowlarr")]
    public async Task<IActionResult> GetProwlarr(CancellationToken ct) => Ok(new ProwlarrSettings(
        await settings.GetAsync(SettingKeys.ProwlarrUrl, ct),
        await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct)));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("prowlarr")]
    public async Task<IActionResult> SetProwlarr([FromBody] ProwlarrSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("Prowlarr") });
        }

        await settings.SetAsync(SettingKeys.ProwlarrUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.ProwlarrApiKey, request.ApiKey, ct);
        return Ok(request);
    }

    public record ProwlarrOptions(string? IndexerIds, string? Categories);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("prowlarr/options")]
    public async Task<IActionResult> GetProwlarrOptions(CancellationToken ct) => Ok(new ProwlarrOptions(
        await settings.GetAsync(SettingKeys.ProwlarrIndexerIds, ct),
        await settings.GetAsync(SettingKeys.ProwlarrCategories, ct)));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("prowlarr/options")]
    public async Task<IActionResult> SetProwlarrOptions([FromBody] ProwlarrOptions request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.ProwlarrIndexerIds, request.IndexerIds, ct);
        await settings.SetAsync(SettingKeys.ProwlarrCategories, request.Categories, ct);
        return Ok(request);
    }

    /// <summary>Proxies Prowlarr's indexer list (with category capabilities) for the settings UI.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("prowlarr/indexers")]
    public async Task<IActionResult> GetProwlarrIndexers(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.ProwlarrUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { error = "Prowlarr is not configured" });
        }

        var indexers = await prowlarr.GetIndexersAsync(url, apiKey, ct);
        return Ok(indexers.Select(i => new
        {
            i.Id,
            i.Name,
            i.Enable,
            i.Protocol,
            Categories = Flatten(i.Capabilities?.Categories)
                .Where(c => c.Name is not null)
                .Select(c => new { c.Id, c.Name })
                .DistinctBy(c => c.Id)
                .OrderBy(c => c.Id)
        }));

        static IEnumerable<Maki.Core.Indexers.ProwlarrClient.ProwlarrCategory> Flatten(
            IEnumerable<Maki.Core.Indexers.ProwlarrClient.ProwlarrCategory>? categories) =>
            categories?.SelectMany(c => new[] { c }.Concat(Flatten(c.SubCategories))) ?? [];
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("prowlarr/test")]
    public async Task<IActionResult> TestProwlarr([FromBody] ProwlarrSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.ProwlarrUrl, ct);
        var apiKey = request.ApiKey ?? await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { error = "URL and API key are required" });
        }

        return await prowlarr.PingAsync(url, apiKey, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "Prowlarr did not respond (check URL/API key)" });
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("qbittorrent")]
    public async Task<IActionResult> GetQBittorrent(CancellationToken ct) => Ok(new QBittorrentSettings(
        await settings.GetAsync(SettingKeys.QBittorrentUrl, ct),
        await settings.GetAsync(SettingKeys.QBittorrentUsername, ct),
        await settings.GetAsync(SettingKeys.QBittorrentPassword, ct),
        await settings.GetAsync(SettingKeys.QBittorrentCategory, ct) ?? "maki",
        await settings.GetAsync(SettingKeys.QBittorrentPathMapFrom, ct),
        await settings.GetAsync(SettingKeys.QBittorrentPathMapTo, ct)));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("qbittorrent")]
    public async Task<IActionResult> SetQBittorrent([FromBody] QBittorrentSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("qBittorrent") });
        }

        await settings.SetAsync(SettingKeys.QBittorrentUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.QBittorrentUsername, request.Username, ct);
        await settings.SetAsync(SettingKeys.QBittorrentPassword, request.Password, ct);
        await settings.SetAsync(SettingKeys.QBittorrentCategory, request.Category, ct);
        await settings.SetAsync(SettingKeys.QBittorrentPathMapFrom, request.PathMapFrom, ct);
        await settings.SetAsync(SettingKeys.QBittorrentPathMapTo, request.PathMapTo, ct);
        return Ok(request);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("qbittorrent/test")]
    public async Task<IActionResult> TestQBittorrent([FromBody] QBittorrentSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.QBittorrentUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "URL is required" });
        }

        var username = request.Username ?? await settings.GetAsync(SettingKeys.QBittorrentUsername, ct) ?? string.Empty;
        var password = request.Password ?? await settings.GetAsync(SettingKeys.QBittorrentPassword, ct) ?? string.Empty;

        return await qbittorrent.PingAsync(url, username, password, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "qBittorrent login failed" });
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("kavita")]
    public async Task<IActionResult> GetKavita(CancellationToken ct) => Ok(new KavitaSettings(
        await settings.GetAsync(SettingKeys.KavitaUrl, ct),
        await settings.GetAsync(SettingKeys.KavitaApiKey, ct),
        await settings.GetAsync(SettingKeys.KavitaPathMapFrom, ct),
        await settings.GetAsync(SettingKeys.KavitaPathMapTo, ct),
        int.TryParse(await settings.GetAsync(SettingKeys.KavitaUserId, ct), out var kavitaUserId)
            ? kavitaUserId
            : null,
        await kavitaUser.ResolveAsync(ct)));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("kavita")]
    public async Task<IActionResult> SetKavita([FromBody] KavitaSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("Kavita") });
        }

        await settings.SetAsync(SettingKeys.KavitaUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.KavitaApiKey, request.ApiKey, ct);
        await settings.SetAsync(SettingKeys.KavitaPathMapFrom, request.PathMapFrom, ct);
        await settings.SetAsync(SettingKeys.KavitaPathMapTo, request.PathMapTo, ct);

        if (request.UserId is { } bound &&
            !await db.Users.AnyAsync(u => u.Id == bound && !u.Disabled && !u.PendingSetup, ct))
        {
            return BadRequest(new { error = "That user does not exist, or cannot sign in" });
        }

        await settings.SetAsync(SettingKeys.KavitaUserId, request.UserId?.ToString(), ct);

        // The resolver caches for a minute; without this the change appears not to have taken.
        kavitaUser.Invalidate();
        return await GetKavita(ct);
    }

    public record KavitaUserSetting(int? UserId);

    /// <summary>
    /// Binds Kavita's reading to one Maki user. Its own endpoint rather than a field on
    /// <c>PUT settings/kavita</c> so the client can change it without round-tripping the URL and API
    /// key — and so a mistyped id can't take the connection down with it.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("kavita/user")]
    public async Task<IActionResult> SetKavitaUser([FromBody] KavitaUserSetting request, CancellationToken ct)
    {
        if (request.UserId is { } bound &&
            !await db.Users.AnyAsync(u => u.Id == bound && !u.Disabled && !u.PendingSetup, ct))
        {
            return BadRequest(new { error = "That user does not exist, or cannot sign in" });
        }

        await settings.SetAsync(SettingKeys.KavitaUserId, request.UserId?.ToString(), ct);

        // The resolver caches for a minute; without this the change appears not to have taken.
        kavitaUser.Invalidate();
        return Ok(new KavitaUserSetting(await kavitaUser.ResolveAsync(ct)));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("kavita/test")]
    public async Task<IActionResult> TestKavita([FromBody] KavitaSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var apiKey = request.ApiKey ?? await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { error = "URL and API key are required" });
        }

        return await kavita.PingAsync(url, apiKey, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "Kavita did not respond (check URL/API key)" });
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("flaresolverr")]
    public async Task<IActionResult> GetFlareSolverr(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        return Ok(new FlareSolverrSettings(url));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("flaresolverr")]
    public async Task<IActionResult> SetFlareSolverr([FromBody] FlareSolverrSettings request, CancellationToken ct)
    {
        if (!IsValidServiceUrl(request.Url))
        {
            return BadRequest(new { error = UrlError("FlareSolverr") });
        }

        await settings.SetAsync(SettingKeys.FlareSolverrUrl, request.Url, ct);
        return Ok(new FlareSolverrSettings(request.Url));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("flaresolverr/test")]
    public async Task<IActionResult> TestFlareSolverr([FromBody] FlareSolverrSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "No FlareSolverr URL configured" });
        }

        var ok = await flareSolverr.PingAsync(url, ct);
        return ok
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "FlareSolverr did not respond" });
    }

    [HttpGet("metadata")]
    public async Task<IActionResult> GetMetadata(CancellationToken ct)
    {
        var useLocalDb = await settings.GetAsync(SettingKeys.MangaBakaUseLocalDb, ct) != "false";
        var status = await mangaBakaDump.GetStatusAsync(ct);
        return Ok(new MetadataSettingsResponse(useLocalDb, status.Present, status.SizeBytes, status.RefreshedAt));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("metadata")]
    public async Task<IActionResult> SetMetadata([FromBody] MetadataSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.MangaBakaUseLocalDb, request.UseLocalDb ? "true" : "false", ct);
        return await GetMetadata(ct);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("metadata/refresh")]
    public async Task<IActionResult> RefreshMetadataDump(CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.TriggerJob(MangaBakaDumpRefreshJob.Key, ct);
        return Ok(new { started = true });
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("updates")]
    public async Task<IActionResult> GetUpdates(CancellationToken ct) => Ok(new UpdateSettings(
        await settings.GetAsync(SettingKeys.UpdatesCheckForUpdates, ct) != "false"));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("updates")]
    public async Task<IActionResult> SetUpdates([FromBody] UpdateSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.UpdatesCheckForUpdates, request.CheckForUpdates ? "true" : "false", ct);
        return Ok(request);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("updates/check")]
    public async Task<IActionResult> CheckForUpdatesNow(CancellationToken ct) => Ok(await updateCheck.CheckAsync(ct));

    public record RecommendationIndexResponse(
        bool ModelPresent, bool DumpPresent, int VectorCount, int? RecommendableTotal,
        bool Running, string Phase, int Embedded, int Scanned,
        DateTime? StartedAt, DateTime? FinishedAt, int LastEmbedded, string? LastError,
        int? EstimatedSecondsRemaining, bool PrebuiltEnabled, 
        DateTime? PrebuiltInstalledAt, string EmbeddingModel, 
        bool UseFullDump, bool ModelSwitching, string? ModelSwitchError);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendationIndex(CancellationToken ct)
    {
        var snap = embeddingStatus.Snapshot();
        var dumpPresent = (await mangaBakaDump.GetStatusAsync(ct)).Present;

        // The recommendable total needs a full-table count; compute it once when idle and
        // cache it on the status object so status polls stay cheap.
        var total = snap.RecommendableTotal;
        if (total is null && !snap.Running && dumpPresent)
        {
            total = await embeddingIndexer.CountRecommendableAsync(ct);
            embeddingStatus.SetTotal(total.Value);
        }

        var prebuiltEnabled = await prebuiltIndex.IsEnabledAsync(ct);
        var prebuiltInstalledAt =
            DateTime.TryParse(
                await settings.GetAsync(SettingKeys.RecommendationsPrebuiltGeneratedAt, ct),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var installedAt)
                ? installedAt
                : (DateTime?)null;

        return Ok(new RecommendationIndexResponse(
            embeddingModel.IsPresent(), dumpPresent, embeddingStore.Count(), total,
            snap.Running, snap.Phase, snap.Embedded, snap.Scanned,
            snap.StartedAt, snap.FinishedAt, snap.LastEmbedded, snap.LastError, 
            snap.EstimatedSecondsRemaining, prebuiltEnabled, prebuiltInstalledAt,
            modelSwitcher.CurrentModel,
            string.Equals(await settings.GetAsync(SettingKeys.MangaBakaUseFullDump, ct), "true", StringComparison.OrdinalIgnoreCase),
            modelSwitcher.Switching, modelSwitcher.LastError));
    }

    public record PrebuiltIndexRequest(bool Enabled);

    /// <summary>
    /// Toggles automatic installation of the published prebuilt index. On by default: the vectors
    /// are derived from the public MangaBaka dump, so downloading them is byte-for-byte equivalent
    /// to spending ~an hour of local CPU.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("recommendations/prebuilt")]
    public async Task<IActionResult> SetPrebuiltIndexEnabled(
        [FromBody] PrebuiltIndexRequest request, CancellationToken ct)
    {
        await settings.SetAsync(
            SettingKeys.RecommendationsPrebuiltEnabled, request.Enabled ? "true" : "false", ct);
        return Ok(new { request.Enabled });
    }

    /// <summary>
    /// Downloads the prebuilt index now, ignoring the "is it newer" check but not the
    /// compatibility ones. Runs inline rather than through the scheduler so the UI can report
    /// exactly why an install was skipped.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("recommendations/prebuilt/download")]
    public async Task<IActionResult> DownloadPrebuiltIndex(CancellationToken ct)
    {
        if (embeddingStatus.Running)
        {
            return Ok(new { installed = false, reason = "An indexing pass is running." });
        }

        var result = await prebuiltIndex.InstallAsync(force: true, ct);
        return Ok(new { installed = result.Installed, reason = result.Reason, rowCount = result.RowCount });
    }

    public record EmbeddingModelRequest(string Model);

    /// <summary>
    /// Switches the embedding model: "base" (the only selectable tier) or "off". Applies live — no
    /// restart, no local re-index: the switch runs in the background, downloading the model's files
    /// and its prebuilt index, and the setting is persisted by the switcher when the switch actually
    /// starts. Poll the recommendations status (<c>modelSwitching</c>) for progress. A no-op when
    /// already on that model.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("recommendations/model")]
    public IActionResult SetEmbeddingModel([FromBody] EmbeddingModelRequest request)
    {
        var result = modelSwitcher.Start(request.Model);
        return Ok(new { model = result.Model, switching = result.Started, reason = result.Reason });
    }

    public record FullDumpRequest(bool UseFullDump);

    /// <summary>
    /// Toggles downloading the larger "full" MangaBaka dump, which carries the MangaUpdates
    /// description the indexer prefers. Only useful on a machine that builds the index locally.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("recommendations/fulldump")]
    public async Task<IActionResult> SetUseFullDump([FromBody] FullDumpRequest request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.MangaBakaUseFullDump, request.UseFullDump ? "true" : "false", ct);
        return Ok(new { request.UseFullDump });
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("recommendations/build")]
    public async Task<IActionResult> BuildRecommendationIndex(CancellationToken ct)
    {
        if (embeddingStatus.Running)
        {
            return Ok(new { started = false, message = "Indexing is already running" });
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var data = new JobDataMap { { EmbeddingIndexJob.ManualTriggerKey, true } };
        await scheduler.TriggerJob(EmbeddingIndexJob.Key, data, ct);
        return Ok(new { started = true });
    }

    public record ScrobbleSettings(
        string? AniListClientId, string? AniListClientSecret,
        string? MalClientId, string? MalClientSecret,
        string? MangaBakaToken,
        string? KitsuClientId, string? KitsuClientSecret, string? KitsuEmail, string? KitsuPassword,
        int IntervalMinutes, bool PlanToRead, string? LibraryIds,
        /// <summary>
        /// Whether the caller may edit the instance half. The client uses it to disable those fields
        /// rather than showing a non-admin inputs whose writes will be dropped.
        /// </summary>
        bool IsAdmin = false);

    /// <summary>
    /// Both halves of the scrobble configuration in one response, because one card in the UI shows
    /// them together — but they are stored in different places and guarded differently.
    /// <para>
    /// The app registrations (AniList/MAL/Kitsu client id and secret), the tick interval and the Kavita
    /// library filter are per-instance and <b>admin-only</b>: they are returned as null to everybody
    /// else rather than masked, since a non-admin has no use for them and a masked secret is still a
    /// length disclosure. The MangaBaka token, the Kitsu account credentials and "add unread as
    /// plan-to-read" name a <em>person's</em> account on the remote site, so they come from the
    /// caller's own <c>UserSettings</c> and need only <c>UseTrackers</c>.
    /// </para>
    /// </summary>
    [Authorize(Policy = Policies.UseTrackers)]
    [HttpGet("scrobble")]
    public async Task<IActionResult> GetScrobble(CancellationToken ct)
    {
        var mine = await userSettings.GetManyAsync(
            [
                SettingKeys.ScrobbleMangaBakaToken,
                SettingKeys.ScrobbleKitsuEmail,
                SettingKeys.ScrobbleKitsuPassword,
                SettingKeys.ScrobblePlanToRead,
            ],
            ct);

        var admin = currentUser.Has(MakiPermission.Admin);
        return Ok(new ScrobbleSettings(
            admin ? await settings.GetAsync(SettingKeys.ScrobbleAniListClientId, ct) : null,
            admin ? await settings.GetAsync(SettingKeys.ScrobbleAniListClientSecret, ct) : null,
            admin ? await settings.GetAsync(SettingKeys.ScrobbleMalClientId, ct) : null,
            admin ? await settings.GetAsync(SettingKeys.ScrobbleMalClientSecret, ct) : null,
            mine.GetValueOrDefault(SettingKeys.ScrobbleMangaBakaToken),
            admin ? await settings.GetAsync(SettingKeys.ScrobbleKitsuClientId, ct) : null,
            admin ? await settings.GetAsync(SettingKeys.ScrobbleKitsuClientSecret, ct) : null,
            mine.GetValueOrDefault(SettingKeys.ScrobbleKitsuEmail),
            mine.GetValueOrDefault(SettingKeys.ScrobbleKitsuPassword),
            int.TryParse(await settings.GetAsync(SettingKeys.ScrobbleIntervalMinutes, ct), out var m) && m >= 5
                ? m
                : Services.ScrobbleService.DefaultIntervalMinutes,
            mine.GetValueOrDefault(SettingKeys.ScrobblePlanToRead) == "true",
            admin ? await settings.GetAsync(SettingKeys.ScrobbleLibraryIds, ct) : null,
            IsAdmin: admin));
    }

    [Authorize(Policy = Policies.UseTrackers)]
    [HttpPut("scrobble")]
    public async Task<IActionResult> SetScrobble([FromBody] ScrobbleSettings request, CancellationToken ct)
    {
        // The caller's own remote accounts, always writable.
        await userSettings.SetAsync(SettingKeys.ScrobbleMangaBakaToken, request.MangaBakaToken, ct);
        await userSettings.SetAsync(SettingKeys.ScrobbleKitsuEmail, request.KitsuEmail, ct);
        await userSettings.SetAsync(SettingKeys.ScrobbleKitsuPassword, request.KitsuPassword, ct);
        await userSettings.SetAsync(
            SettingKeys.ScrobblePlanToRead, request.PlanToRead ? "true" : "false", ct);

        // The instance half is silently ignored for a non-admin rather than rejected: the client sends
        // the whole DTO back, and failing the request would stop a reader-only account from saving
        // their own Kitsu password just because nulls came along for the ride.
        if (!currentUser.Has(MakiPermission.Admin))
        {
            return await GetScrobble(ct);
        }

        await settings.SetAsync(SettingKeys.ScrobbleAniListClientId, request.AniListClientId, ct);
        await settings.SetAsync(SettingKeys.ScrobbleAniListClientSecret, request.AniListClientSecret, ct);
        await settings.SetAsync(SettingKeys.ScrobbleMalClientId, request.MalClientId, ct);
        await settings.SetAsync(SettingKeys.ScrobbleMalClientSecret, request.MalClientSecret, ct);
        // Per Kitsu API documentation, Client ID and Secret is not yet implemented and these temp values should be used.
        await settings.SetAsync(SettingKeys.ScrobbleKitsuClientId, "dd031b32d2f56c990b1425efe6c42ad847e7fe3ab46bf1299f05ecd856bdb7dd", ct);
        await settings.SetAsync(SettingKeys.ScrobbleKitsuClientSecret, "54d7307928f63414defd96399fc31ba847961ceaecef3a5fd93144e960c0e151", ct);
        await settings.SetAsync(SettingKeys.ScrobbleIntervalMinutes,
            Math.Max(request.IntervalMinutes, 5).ToString(), ct);

        await settings.SetAsync(SettingKeys.ScrobbleLibraryIds, request.LibraryIds, ct);
        return await GetScrobble(ct);
    }

    /// <summary>
    /// No longer returns an API key. There is no instance-wide key: credentials belong to users, are
    /// created under Account, and only their SHA-256 digest is ever stored — so there is nothing here
    /// to hand back, and the rotate endpoint that used to sit beside this is gone with it.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("general")]
    public IActionResult GetGeneral()
    {
        return Ok(new { port = configFile.Config.Port });
    }

    /// <summary>
    /// The <c>auth.*</c> settings. Applied at startup — the session cookie's Secure flag, HSTS, the
    /// trusted-proxy list and the lockout thresholds all configure objects the host builds once — so
    /// a change here takes effect on restart, and the UI says so.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("security")]
    public async Task<IActionResult> GetSecurity(CancellationToken ct) => Ok(new SecuritySettings(
        await settings.GetAsync(SettingKeys.AuthRequireHttps, ct) == "true",
        await settings.GetAsync(SettingKeys.AuthTrustedProxies, ct) ?? string.Empty,
        int.TryParse(await settings.GetAsync(SettingKeys.AuthLockoutMaxAttempts, ct), out var attempts)
            ? attempts : AuthRuntimeOptions.DefaultLockoutMaxAttempts,
        int.TryParse(await settings.GetAsync(SettingKeys.AuthLockoutMinutes, ct), out var minutes)
            ? minutes : AuthRuntimeOptions.DefaultLockoutMinutes,
        int.TryParse(await settings.GetAsync(SettingKeys.AuthSessionDays, ct), out var days)
            ? days : AuthRuntimeOptions.DefaultSessionDays));

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("security")]
    public async Task<IActionResult> SetSecurity([FromBody] SecuritySettings request, CancellationToken ct)
    {
        foreach (var entry in (request.TrustedProxies ?? string.Empty)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Validated on save rather than silently ignored at startup: a typo here means forwarded
            // headers are quietly dropped, which shows up much later as every audit-log entry and
            // every rate-limit bucket carrying the proxy's address instead of the client's.
            var address = entry.Contains('/') ? entry.Split('/', 2)[0] : entry;
            if (!System.Net.IPAddress.TryParse(address, out _))
            {
                return BadRequest(new { error = $"\"{entry}\" is not an IP address or CIDR network" });
            }
        }

        await settings.SetAsync(SettingKeys.AuthRequireHttps, request.RequireHttps ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.AuthTrustedProxies, request.TrustedProxies, ct);
        // Zero is meaningful (lockout off), so it is clamped at zero rather than at one.
        await settings.SetAsync(SettingKeys.AuthLockoutMaxAttempts,
            Math.Max(0, request.LockoutMaxAttempts).ToString(), ct);
        await settings.SetAsync(SettingKeys.AuthLockoutMinutes,
            Math.Max(1, request.LockoutMinutes).ToString(), ct);
        await settings.SetAsync(SettingKeys.AuthSessionDays,
            Math.Max(1, request.SessionDays).ToString(), ct);

        return await GetSecurity(ct);
    }

    /// <summary>
    /// The <c>auth.oidc*</c> settings. Applied at startup for the same reason the rest of
    /// <c>auth.*</c> is: the OpenID Connect handler is built once and fetches the provider's
    /// discovery document on first use.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("oidc")]
    public async Task<IActionResult> GetOidc(CancellationToken ct)
    {
        // One query against the shared key list, rather than a local copy of it read a key at a
        // time. Both halves matter: OidcRuntimeOptions.Keys is named once precisely so the readers
        // cannot drift apart when a key is added, and SettingsService opens a fresh scope and
        // DbContext per key — eleven of each for one settings card.
        var values = await db.AppConfig
            .AsNoTracking()
            .Where(c => OidcRuntimeOptions.Keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => (string?)c.Value, ct);

        return Ok(new OidcSettings(
            values.GetValueOrDefault(SettingKeys.AuthOidcEnabled) == "true",
            values.GetValueOrDefault(SettingKeys.AuthOidcAuthority) ?? string.Empty,
            values.GetValueOrDefault(SettingKeys.AuthOidcClientId) ?? string.Empty,
            values.GetValueOrDefault(SettingKeys.AuthOidcClientSecret) ?? string.Empty,
            values.GetValueOrDefault(SettingKeys.AuthOidcScopes) ?? OidcRuntimeOptions.DefaultScopes,
            values.GetValueOrDefault(SettingKeys.AuthOidcDisplayName) ?? OidcRuntimeOptions.DefaultDisplayName,
            values.GetValueOrDefault(SettingKeys.AuthOidcOnly) == "true",
            values.GetValueOrDefault(SettingKeys.AuthOidcAutoProvision) == "true",
            values.GetValueOrDefault(SettingKeys.AuthOidcUsernameClaim) ?? OidcRuntimeOptions.DefaultUsernameClaim,
            values.GetValueOrDefault(SettingKeys.AuthOidcAdminClaim) ?? string.Empty,
            values.GetValueOrDefault(SettingKeys.AuthOidcPermissionClaim) ?? string.Empty,
            OidcRuntimeOptions.CallbackPath,
            OidcRuntimeOptions.BreakGlassSet));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("oidc")]
    public async Task<IActionResult> SetOidc([FromBody] OidcSettings request, CancellationToken ct)
    {
        var authority = (request.Authority ?? string.Empty).Trim().TrimEnd('/');

        // Validated on save rather than at startup, where a typo means the login button leads to a
        // discovery failure the user cannot read and the admin cannot see.
        if (authority.Length > 0 &&
            !(Uri.TryCreate(authority, UriKind.Absolute, out var issuer) &&
              (issuer.Scheme == Uri.UriSchemeHttp || issuer.Scheme == Uri.UriSchemeHttps)))
        {
            return BadRequest(new { error = UrlError("The identity provider's issuer") });
        }

        if (request.Enabled && (authority.Length == 0 || string.IsNullOrWhiteSpace(request.ClientId)))
        {
            return BadRequest(new { error = "An issuer URL and a client id are required to enable single sign-on" });
        }

        await settings.SetAsync(SettingKeys.AuthOidcEnabled, request.Enabled ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.AuthOidcAuthority, authority, ct);
        await settings.SetAsync(SettingKeys.AuthOidcClientId, (request.ClientId ?? string.Empty).Trim(), ct);
        await settings.SetAsync(SettingKeys.AuthOidcClientSecret, request.ClientSecret, ct);
        await settings.SetAsync(SettingKeys.AuthOidcScopes, request.Scopes, ct);
        await settings.SetAsync(SettingKeys.AuthOidcDisplayName, request.DisplayName, ct);
        await settings.SetAsync(SettingKeys.AuthOidcOnly, request.OidcOnly ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.AuthOidcAutoProvision, request.AutoProvision ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.AuthOidcUsernameClaim, request.UsernameClaim, ct);
        await settings.SetAsync(SettingKeys.AuthOidcAdminClaim, request.AdminClaim, ct);
        await settings.SetAsync(SettingKeys.AuthOidcPermissionClaim, request.PermissionClaim, ct);

        return await GetOidc(ct);
    }
}
