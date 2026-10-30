using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using System.Text.Json;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Quartz;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/scrobble")]
// Connecting and pushing to a tracker is per-account behaviour, so it rides on UseTrackers rather
// than on admin. The OAuth callback below opts out — the provider redirects the user's browser there
// and it is authenticated by the random state instead.
[Authorize(Policy = Policies.UseTrackers)]
public class ScrobbleController(
    ILocalizer localizer,
    ScrobbleService scrobbler,
    AnimeSignalSources animeSources,
    IScrobbleTokenStore tokens,
    SettingsService settings,
    IUserSettings userSettings,
    MakiDbContext db,
    ICurrentUser currentUser,
    ISchedulerFactory schedulerFactory) : ControllerBase
{
    /// <summary>
    /// Everything here acts for the signed-in caller: their tokens, their mappings, their toggles.
    /// The one exception is the OAuth callback, which reads the user id out of the in-flight session
    /// because no cookie reaches it.
    /// </summary>
    private int UserId => currentUser.UserId;

    /// <param name="AnimeList">
    /// Whether this tracker can hand over an anime list at all, which is what decides if the client
    /// draws the anime-taste switch. AniList and MyAnimeList can; Kitsu, MangaBaka and Kavita
    /// cannot, and a switch on those would be a control with nothing behind it.
    /// </param>
    /// <param name="AnimeSignals">The per-tracker anime-taste toggle. Meaningless when <paramref name="AnimeList"/> is false.</param>
    /// <param name="BackOffUntil">
    /// When a tracker that is refusing calls will be tried again, or null. The client renders the
    /// remaining time itself, so the answer is right however long the page has been open.
    /// </param>
    /// <param name="SavedDetails">
    /// For a tracker each reader signs in to with their own details (MangaBaka, MangaUpdates): whether
    /// this reader has saved any. Null for the rest. Those trackers are always "configured", so without
    /// it a reader who never entered anything is told their saved details are wrong.
    /// </param>
    public record ConnectionDto(
        string Service, string Label, bool Configured, bool Connected, string? Username, bool OAuth,
        bool SyncReading, bool SyncRatings, bool AnimeList, bool AnimeSignals, bool? SavedDetails = null,
        DateTimeOffset? BackOffUntil = null, bool SyncSoFar = false);

    public record MatchRequest(int KavitaSeriesId, string Service, string RemoteId);
    public record IgnoreRequest(int KavitaSeriesId, string Service);
    /// <param name="Anime">
    /// Nullable and written only when present: the switch shows on two of the five tracker blocks,
    /// so a client saving the other three sends no opinion about it rather than an accidental off.
    /// </param>
    /// <param name="SoFar">Nullable and written only when present, like <paramref name="Anime"/>.</param>
    public record PreferencesRequest(bool Reading, bool Ratings, bool? Anime = null, bool? SoFar = null);

    /// <summary>
    /// The answer to "send so-far scores to these trackers too?". <paramref name="SeriesId"/> is the
    /// series whose rating raised the question: on yes its "so far" score goes out straight away.
    /// </summary>
    public record SoFarSyncRequest(string[] Services, bool Enabled, int? SeriesId = null);
    public record ApplyRatingImportRequest(int[] SeriesIds);

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromServices] IMemoryCache cache, CancellationToken ct)
    {
        var kavitaConfigured =
            !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
            !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.KavitaApiKey, ct));
        var connections = new List<ConnectionDto>
        {
            new("kavita", "Kavita", kavitaConfigured,
                kavitaConfigured && await scrobbler.KavitaConnectedAsync(ct),
                currentUser.Has(MakiPermission.Admin) ? await settings.GetAsync(SettingKeys.KavitaUrl, ct) : null,
                OAuth: false,
                SyncReading: true, SyncRatings: false, AnimeList: false, AnimeSignals: false),
        };

        foreach (var tracker in scrobbler.Trackers)
        {
            var configured = await tracker.ConfiguredAsync(ct);
            var connected = configured && await tracker.AuthenticatedAsync(UserId, ct);
            connections.Add(new ConnectionDto(
                tracker.Name, tracker.Label, configured, connected,
                connected ? await tracker.UsernameAsync(UserId, ct) : null, tracker.UsesOAuth,
                await scrobbler.SyncReadingEnabledAsync(UserId, tracker.Name, ct),
                await scrobbler.SyncRatingsEnabledAsync(UserId, tracker.Name, ct),
                AnimeList: tracker is IAnimeListSource,
                AnimeSignals: await animeSources.EnabledForAsync(UserId, tracker.Name, ct),
                SavedDetails: await SavedDetailsAsync(tracker.Name, ct),
                BackOffUntil: tracker.BackOffUntil,
                SyncSoFar: await scrobbler.SyncSoFarEnabledAsync(UserId, tracker.Name, ct)));
        }

        var lastSync = await scrobbler.LastSyncAtAsync(ct);
        var interval = await scrobbler.IntervalMinutesAsync(ct);

        var recentKavita = await db.ScrobbleSyncStates.AsNoTracking()
            .OrderByDescending(s => s.SyncedAt)
            .Take(40)
            .Select(s => new RecentRow(s.Title, s.Service, s.Chapter, s.Volume, s.Status, s.SyncedAt, s.Error, null))
            .ToListAsync(ct);

        var unmatchedRows = await db.ScrobbleUnmatched.AsNoTracking()
            .OrderBy(u => u.Title).ThenBy(u => u.Service)
            .ToListAsync(ct);

        var recentNative = await db.SeriesScrobbleStates.AsNoTracking()
            .OrderByDescending(s => s.SyncedAt)
            .Take(40)
            .Join(db.Series, s => s.SeriesId, sr => sr.Id,
                (s, sr) => new RecentRow(sr.Title, s.Service, s.Chapter, s.Volume, s.Status, s.SyncedAt, s.Error, sr.Id))
            .ToListAsync(ct);
        var recentRows = recentKavita.Concat(recentNative).OrderByDescending(s => s.At).Take(40).ToList();

        // Kavita rows name a Kavita series, not one of ours. They are tied to a library series the
        // way the series page ties them (normalized title or folder name), only to show its cover.
        var library = await LibrarySeriesByTitleAsync(
            cache, recentRows.Where(r => r.SeriesId is null).Select(r => r.Title).Concat(unmatchedRows.Select(u => u.Title)),
            ct);
        var nativeIds = recentRows.Where(r => r.SeriesId is not null).Select(r => r.SeriesId!.Value).Distinct().ToList();
        var covers = await db.Series.AsNoTracking()
            .Where(s => nativeIds.Contains(s.Id))
            .Select(s => new LibrarySeries(s.Id, s.CoverPath, s.LastMetadataRefresh))
            .ToDictionaryAsync(s => s.Id, ct);
        LibrarySeries? SeriesFor(int? id, string title) =>
            id is { } known ? covers.GetValueOrDefault(known) : library.GetValueOrDefault(ScrobbleMatching.NormalizeTitle(title));

        var recent = recentRows.Select(r =>
        {
            var series = SeriesFor(r.SeriesId, r.Title);
            return new
            {
                r.Title,
                r.Service,
                r.Chapter,
                r.Volume,
                r.Status,
                r.At,
                Error = ScrobbleService.RenderError(
                    r.Error, scrobbler.Trackers.FirstOrDefault(t => t.Name == r.Service)?.Label ?? r.Service, localizer),
                SeriesId = series?.Id,
                CoverUrl = series is null ? null : SeriesDto.CoverUrlFor(series.Id, series.CoverPath, series.Refreshed),
            };
        }).ToList();

        var unmatched = unmatchedRows.Select(u =>
        {
            var series = SeriesFor(null, u.Title);
            return new
            {
                u.KavitaSeriesId,
                u.Service,
                u.Title,
                u.Reason,
                Candidates = JsonSerializer.Deserialize<List<ScrobbleService.CandidateDto>>(u.CandidatesJson) ?? [],
                SeriesId = series?.Id,
                CoverUrl = series is null ? null : SeriesDto.CoverUrlFor(series.Id, series.CoverPath, series.Refreshed),
            };
        });

        // Rendered here rather than at the raise site: this log is one person's own, and they may
        // have changed language since the line was written. A row with no key is somebody else's
        // words (a tracker's, Kavita's) or predates the keys, and is served as it was stored.
        var logRows = await db.ScrobbleLog.AsNoTracking()
            .OrderByDescending(l => l.Id)
            .Take(60)
            .Select(l => new { l.Timestamp, l.Level, l.Service, l.Title, l.MessageKey, l.ParamsJson, l.Message })
            .ToListAsync(ct);
        var log = logRows.Select(l => new
        {
            l.Timestamp,
            l.Level,
            l.Service,
            l.Title,
            Message = l.MessageKey is { Length: > 0 } key
                ? localizer.Get(key, WithRenderedStatus(ScrobbleLogParams(l.ParamsJson)))
                : l.Message,
        }).ToList();

        return Ok(new
        {
            Connections = connections,
            scrobbler.Running,
            LastSyncAt = lastSync,
            NextSyncAt = lastSync?.AddMinutes(interval),
            IntervalMinutes = interval,
            PlanToRead = await userSettings.GetAsync(SettingKeys.ScrobblePlanToRead, ct) == "true",
            Recent = recent,
            Unmatched = unmatched,
            Log = log,
        });
    }

    private async Task<bool?> SavedDetailsAsync(string service, CancellationToken ct)
    {
        switch (service)
        {
            case "mangabaka":
                return !string.IsNullOrWhiteSpace(await userSettings.GetAsync(SettingKeys.ScrobbleMangaBakaToken, ct));
            case "mangaupdates":
                return !string.IsNullOrWhiteSpace(await userSettings.GetAsync(SettingKeys.ScrobbleMangaUpdatesUsername, ct)) &&
                       !string.IsNullOrEmpty(await userSettings.GetAsync(SettingKeys.ScrobbleMangaUpdatesPassword, ct));
            default:
                return null;
        }
    }

    private sealed record RecentRow(
        string Title, string Service, int Chapter, int Volume, string? Status, DateTime At, string? Error, int? SeriesId);

    private sealed record LibrarySeries(int Id, string? CoverPath, DateTime? Refreshed);

    /// <summary>
    /// Library series keyed by normalized title and folder name, for the titles asked about. Every
    /// series is read because the key is computed in memory (id, names and cover columns only), so the
    /// whole map is kept per user for a minute: the page polls this every few seconds.
    /// </summary>
    private async Task<Dictionary<string, LibrarySeries>> LibrarySeriesByTitleAsync(
        IMemoryCache cache, IEnumerable<string> titles, CancellationToken ct)
    {
        var wanted = titles.Select(ScrobbleMatching.NormalizeTitle).Where(k => k.Length > 0).ToHashSet();
        var found = new Dictionary<string, LibrarySeries>();
        if (wanted.Count == 0)
        {
            return found;
        }

        var map = await cache.GetOrCreateAsync($"scrobble:library-titles:{UserId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var all = await db.Series.AsNoTracking()
                .OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.Title, s.FolderName, s.CoverPath, s.LastMetadataRefresh })
                .ToListAsync(ct);
            var byKey = new Dictionary<string, LibrarySeries>();
            foreach (var s in all)
            {
                foreach (var name in new[] { s.Title, s.FolderName })
                {
                    var key = ScrobbleMatching.NormalizeTitle(name ?? "");
                    if (key.Length > 0)
                    {
                        byKey.TryAdd(key, new LibrarySeries(s.Id, s.CoverPath, s.LastMetadataRefresh));
                    }
                }
            }

            return byKey;
        });

        foreach (var key in wanted)
        {
            if (map!.TryGetValue(key, out var series))
            {
                found[key] = series;
            }
        }

        return found;
    }

    /// <summary>
    /// A status arrives as the bare word the tracker's API uses ("plan_to_read"), which is a wire
    /// value, not something to show somebody. Swapped for its own message before the line renders.
    /// </summary>
    private Dictionary<string, object?> WithRenderedStatus(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("status", out var status) && status is string name
            && ScrobbleService.StatusKeys.TryGetValue(name, out var key))
        {
            args["status"] = localizer.Get(key);
        }

        return args;
    }

    /// <summary>
    /// Values for a keyed log line. Never throws: a row whose parameters cannot be read still has a
    /// message worth showing, with its placeholders unfilled, and losing the whole log is worse.
    /// </summary>
    private static Dictionary<string, object?> ScrobbleLogParams(string? json)
    {
        var into = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json)) return into;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                into[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetString(),
                };
            }
        }
        catch (JsonException)
        {
        }

        return into;
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        if (scrobbler.Running)
        {
            return Ok(new { message = localizer.Get("scrobble.action.syncRunning") });
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.TriggerJob(ScrobbleJob.Key, new JobDataMap { { ScrobbleJob.ForceKey, true } }, ct);
        return Ok(new { message = localizer.Get("scrobble.action.syncStarted") });
    }

    /// <summary>Manually maps a Kavita series to a tracker id (accepts a pasted series URL too).</summary>
    [HttpPost("match")]
    public async Task<IActionResult> Match([FromBody] MatchRequest request, CancellationToken ct)
    {
        if (scrobbler.FindTracker(request.Service) is null)
        {
            return this.Fail(localizer, "error.scrobble.unknownService");
        }

        var remoteId = request.RemoteId.Trim();
        if (request.Service == "mangaupdates")
        {
            if (!MangaUpdatesIds.TryParse(remoteId, out remoteId!))
            {
                return this.Fail(localizer, "error.scrobble.invalidMangaUpdatesId");
            }
        }
        else if (!remoteId.All(char.IsAsciiDigit) || remoteId.Length == 0)
        {
            var ids = ScrobbleMatching.ParseWebLinks([remoteId]);
            if (!ids.TryGetValue(request.Service, out remoteId!))
            {
                return this.Fail(localizer, "error.scrobble.invalidRemoteId");
            }
        }

        var title = await db.ScrobbleUnmatched.AsNoTracking()
            .Where(u => u.KavitaSeriesId == request.KavitaSeriesId && u.Service == request.Service)
            .Select(u => u.Title)
            .FirstOrDefaultAsync(ct) ?? "";
        await scrobbler.SaveMappingAsync(
            UserId, request.KavitaSeriesId, request.Service, remoteId, "manual", title, ct);
        return Ok(new { message = localizer.Get("scrobble.action.mapped", new { remoteId }) });
    }

    /// <summary>Ignores a series for one service (stored as a mapping with an empty remote id).</summary>
    [HttpPost("ignore")]
    public async Task<IActionResult> Ignore([FromBody] IgnoreRequest request, CancellationToken ct)
    {
        await scrobbler.SaveMappingAsync(UserId, request.KavitaSeriesId, request.Service, "", "ignored", "", ct);
        return Ok(new { message = localizer.Get("scrobble.action.ignored") });
    }

    // ---- per-tracker preferences ----

    /// <summary>Sets the per-tracker "scrobble reading" / "sync ratings" toggles.</summary>
    [HttpPut("preferences/{service}")]
    public async Task<IActionResult> SetPreferences(
        string service, [FromBody] PreferencesRequest request, CancellationToken ct)
    {
        if (scrobbler.FindTracker(service) is null)
        {
            return this.Fail(localizer, "error.scrobble.unknownService");
        }

        // Per-user: one reader turning off AniList pushes must not silence everyone else's.
        await userSettings.SetAsync(
            SettingKeys.ScrobbleReadingKey(service), request.Reading ? "true" : "false", ct);
        await userSettings.SetAsync(
            SettingKeys.ScrobbleRatingsKey(service), request.Ratings ? "true" : "false", ct);

        if (request.SoFar is { } soFar)
        {
            await userSettings.SetAsync(SettingKeys.ScrobbleSoFarKey(service), soFar ? "true" : "false", ct);
        }

        if (request.Anime is { } anime)
        {
            await userSettings.SetAsync(
                SettingKeys.RecommendationsAnimeSignalsSourceKey(service), anime ? "true" : "false", ct);
            if (!anime)
            {
                // Now rather than at the next sync. The stored rows are what the recommender reads,
                // so leaving them would keep this tracker's list in somebody's taste for up to a
                // day after they switched it off, which is not what a switch means.
                var stale = await db.AnimeSignals
                    .Where(x => x.UserId == UserId && x.Service == service).ToListAsync(ct);
                if (stale.Count > 0)
                {
                    db.AnimeSignals.RemoveRange(stale);
                    await db.SaveChangesAsync(ct);
                    await RecommendationFeedbackService.BumpAsync(db, UserId, feedback: false, signal: true, ct);
                }
            }
        }

        return Ok(new
        {
            service,
            reading = request.Reading,
            ratings = request.Ratings,
            anime = request.Anime,
            soFar = request.SoFar,
        });
    }

    [HttpPut("sofar")]
    public async Task<IActionResult> SetSoFarSync([FromBody] SoFarSyncRequest request, CancellationToken ct)
    {
        var saved = new List<string>();
        foreach (var service in request.Services.Where(s => scrobbler.FindTracker(s) is not null).Distinct())
        {
            // "So far" sits under "Sync ratings": turning it on where that is off would also mark the
            // tracker as asked, so it would never get the question once ratings are switched back on.
            if (request.Enabled && !await scrobbler.SyncRatingsEnabledAsync(UserId, service, ct))
            {
                continue;
            }

            saved.Add(service);
            await userSettings.SetAsync(SettingKeys.ScrobbleSoFarKey(service), request.Enabled ? "true" : "false", ct);
        }

        if (request.Enabled && saved.Count > 0 && request.SeriesId is { } seriesId)
        {
            var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
            var soFar = await db.UserSeriesStates
                .Where(s => s.SeriesId == seriesId && s.Rating == null)
                .Select(s => s.ProvisionalRating)
                .FirstOrDefaultAsync(ct);
            if (series is not null && soFar is { } score)
            {
                scrobbler.QueueRatingPush(UserId, series, score, soFar: true);
            }
        }

        return Ok(new { services = saved, enabled = request.Enabled });
    }

    // ---- rating import (preview → apply) ----

    /// <summary>Starts a background preview of the ratings held on the given service.</summary>
    [HttpPost("import-ratings/{service}/preview")]
    public IActionResult StartRatingImport(string service)
    {
        if (scrobbler.FindTracker(service) is null)
        {
            return this.Fail(localizer, "error.scrobble.unknownService");
        }

        scrobbler.QueueRatingImportPreview(UserId, currentUser.AllRootFolders, service);
        return Ok(new { started = true });
    }

    /// <summary>Polls the in-flight/last preview for a service.</summary>
    [HttpGet("import-ratings/{service}")]
    public IActionResult GetRatingImport(string service)
    {
        var state = scrobbler.GetRatingImport(UserId, service);
        return Ok(new
        {
            state.Running,
            state.ComputedAt,
            state.Error,
            Items = state.Items,
        });
    }

    /// <summary>Applies the chosen previewed remote scores to the local series ratings.</summary>
    [HttpPost("import-ratings/{service}/apply")]
    public async Task<IActionResult> ApplyRatingImport(
        string service, [FromBody] ApplyRatingImportRequest request, CancellationToken ct)
    {
        var applied = await scrobbler.ApplyRatingImportAsync(UserId, currentUser.AllRootFolders, service, request.SeriesIds, ct);
        return Ok(new { applied });
    }

    // ---- OAuth ----

    /// <summary>
    /// Returns the provider authorize URL for the frontend to navigate to. The
    /// redirect URI is built from the SPA's own origin (passed by the frontend) so
    /// the provider redirects the browser back to the site the user is browsing —
    /// register {origin}/api/v1/scrobble/oauth/{service} at the provider.
    /// </summary>
    [HttpGet("auth/{service}/start")]
    public async Task<IActionResult> AuthStart(string service, [FromQuery] string? origin, CancellationToken ct)
    {
        var redirectUri = $"{ResolveOrigin(origin)}/api/v1/scrobble/oauth/{service}";
        switch (service)
        {
            case "anilist":
                if (!await scrobbler.AniList.ConfiguredAsync(ct))
                {
                    return this.Fail(localizer, "error.scrobble.aniListNotConfigured");
                }

                {
                    var session = scrobbler.StartOAuthSession(UserId, service, redirectUri);
                    return Ok(new { url = await scrobbler.AniList.AuthorizeUrlAsync(redirectUri, session.State, ct) });
                }

            case "mal":
                if (!await scrobbler.Mal.ConfiguredAsync(ct))
                {
                    return this.Fail(localizer, "error.scrobble.malNotConfigured");
                }

                {
                    var session = scrobbler.StartOAuthSession(UserId, service, redirectUri);
                    return Ok(new
                    {
                        url = await scrobbler.Mal.AuthorizeUrlAsync(redirectUri, session.State, session.CodeVerifier, ct),
                    });
                }

            default:
                return this.Fail(localizer, "error.scrobble.unknownService");
        }
    }

    /// <summary>
    /// The origin to build the OAuth redirect URI from: the SPA-supplied <paramref name="origin"/>
    /// when it's a well-formed http(s) URL (so the redirect lands back on the browsed site — the
    /// SPA and API can be on different hosts), otherwise the API request's own scheme/host.
    /// </summary>
    private string ResolveOrigin(string? origin) =>
        !string.IsNullOrWhiteSpace(origin) &&
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? $"{uri.Scheme}://{uri.Authority}"
            : $"{Request.Scheme}://{Request.Host}";

    /// <summary>
    /// OAuth redirect target. Anonymous by necessity: the provider redirects the user's browser here
    /// and controls none of Maki's credentials. The random <c>state</c>, bound to the in-flight
    /// session that started the flow, is what authenticates the request.
    /// <para>
    /// Being anonymous is also why this must stay a GET that only exchanges a code and redirects —
    /// it is the one unauthenticated write path in the app, and the state is the whole of its
    /// security. Per-user tracker tokens bind that state to the initiating user id.
    /// </para>
    /// </summary>
    [HttpGet("/api/v1/scrobble/oauth/{service}")]
    [AllowAnonymous]
    public async Task<IActionResult> OAuthCallback(
        string service, [FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Redirect("/scrobble?error=" + Uri.EscapeDataString(
                localizer.Get("error.scrobble.oauthCancelled")));
        }

        var session = scrobbler.TakeOAuthSession(service, state);
        if (session is null)
        {
            return Redirect("/scrobble?error=" + Uri.EscapeDataString(
                localizer.Get("error.scrobble.oauthStateMismatch")));
        }

        try
        {
            switch (service)
            {
                // session.UserId, never the request's — there is no authenticated caller here, and
                // the state that matched is the only thing that says whose flow this was.
                case "anilist":
                    await scrobbler.AniList.ExchangeCodeAsync(session.UserId, code, session.RedirectUri, ct);
                    break;
                case "mal":
                    await scrobbler.Mal.ExchangeCodeAsync(
                        session.UserId, code, session.CodeVerifier, session.RedirectUri, ct);
                    break;
                default:
                    return Redirect("/scrobble?error=" + Uri.EscapeDataString(
                        localizer.Get("error.scrobble.unknownService")));
            }
        }
        catch (TrackerException e)
        {
            return Redirect("/scrobble?error=" + Uri.EscapeDataString(e.Message));
        }

        return Redirect($"/scrobble?connected={service}");
    }

    [HttpPost("auth/{service}/disconnect")]
    public async Task<IActionResult> Disconnect(string service, CancellationToken ct)
    {
        if (service is not ("anilist" or "mal" or "kitsu"))
        {
            return this.Fail(localizer, "error.scrobble.unknownService");
        }

        await tokens.DeleteAsync(UserId, service, ct);
        return Ok(new { message = localizer.Get("scrobble.action.disconnected") });
    }
}
