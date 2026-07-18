using System.Text.Json;
using Mangarr.Api.Jobs;
using Mangarr.Api.Services;
using Mangarr.Core.Configuration;
using Mangarr.Core.Scrobbling;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/scrobble")]
public class ScrobbleController(
    ScrobbleService scrobbler,
    IScrobbleTokenStore tokens,
    SettingsService settings,
    MangarrDbContext db,
    ISchedulerFactory schedulerFactory) : ControllerBase
{
    public record ConnectionDto(
        string Service, string Label, bool Configured, bool Connected, string? Username, bool OAuth,
        bool SyncReading, bool SyncRatings);

    public record MatchRequest(int KavitaSeriesId, string Service, string RemoteId);
    public record IgnoreRequest(int KavitaSeriesId, string Service);
    public record PreferencesRequest(bool Reading, bool Ratings);
    public record ApplyRatingImportRequest(int[] SeriesIds);

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var kavitaConfigured =
            !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
            !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.KavitaApiKey, ct));
        var connections = new List<ConnectionDto>
        {
            new("kavita", "Kavita", kavitaConfigured,
                kavitaConfigured && await scrobbler.KavitaConnectedAsync(ct),
                await settings.GetAsync(SettingKeys.KavitaUrl, ct), OAuth: false,
                SyncReading: true, SyncRatings: false),
        };

        foreach (var tracker in scrobbler.Trackers)
        {
            var configured = await tracker.ConfiguredAsync(ct);
            var connected = configured && await tracker.AuthenticatedAsync(ct);
            connections.Add(new ConnectionDto(
                tracker.Name, tracker.Label, configured, connected,
                connected ? await tracker.UsernameAsync(ct) : null, tracker.UsesOAuth,
                await scrobbler.SyncReadingEnabledAsync(tracker.Name, ct),
                await scrobbler.SyncRatingsEnabledAsync(tracker.Name, ct)));
        }

        var lastSync = await scrobbler.LastSyncAtAsync(ct);
        var interval = await scrobbler.IntervalMinutesAsync(ct);

        var recent = await db.ScrobbleSyncStates.AsNoTracking()
            .OrderByDescending(s => s.SyncedAt)
            .Take(40)
            .Select(s => new { s.Title, s.Service, s.Chapter, s.Volume, s.Status, At = s.SyncedAt, s.Error })
            .ToListAsync(ct);

        var unmatched = (await db.ScrobbleUnmatched.AsNoTracking()
                .OrderBy(u => u.Title).ThenBy(u => u.Service)
                .ToListAsync(ct))
            .Select(u => new
            {
                u.KavitaSeriesId,
                u.Service,
                u.Title,
                u.Reason,
                Candidates = JsonSerializer.Deserialize<List<ScrobbleService.CandidateDto>>(u.CandidatesJson) ?? [],
            });

        var log = await db.ScrobbleLog.AsNoTracking()
            .OrderByDescending(l => l.Id)
            .Take(60)
            .Select(l => new { l.Timestamp, l.Level, l.Service, l.Title, l.Message })
            .ToListAsync(ct);

        return Ok(new
        {
            Connections = connections,
            scrobbler.Running,
            LastSyncAt = lastSync,
            NextSyncAt = lastSync?.AddMinutes(interval),
            IntervalMinutes = interval,
            PlanToRead = await settings.GetAsync(SettingKeys.ScrobblePlanToRead, ct) == "true",
            Recent = recent,
            Unmatched = unmatched,
            Log = log,
        });
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        if (scrobbler.Running)
        {
            return Ok(new { message = "sync already running" });
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.TriggerJob(ScrobbleJob.Key, new JobDataMap { { ScrobbleJob.ForceKey, true } }, ct);
        return Ok(new { message = "sync started" });
    }

    /// <summary>Manually maps a Kavita series to a tracker id (accepts a pasted series URL too).</summary>
    [HttpPost("match")]
    public async Task<IActionResult> Match([FromBody] MatchRequest request, CancellationToken ct)
    {
        if (scrobbler.FindTracker(request.Service) is null)
        {
            return BadRequest(new { error = "unknown service" });
        }

        var remoteId = request.RemoteId.Trim();
        if (!remoteId.All(char.IsAsciiDigit) || remoteId.Length == 0)
        {
            var ids = ScrobbleMatching.ParseWebLinks([remoteId]);
            if (!ids.TryGetValue(request.Service, out remoteId!))
            {
                return BadRequest(new { error = "expected a numeric id or a series URL for that service" });
            }
        }

        var title = await db.ScrobbleUnmatched.AsNoTracking()
            .Where(u => u.KavitaSeriesId == request.KavitaSeriesId && u.Service == request.Service)
            .Select(u => u.Title)
            .FirstOrDefaultAsync(ct) ?? "";
        await scrobbler.SaveMappingAsync(request.KavitaSeriesId, request.Service, remoteId, "manual", title, ct);
        return Ok(new { message = $"mapped to {remoteId} — will sync on the next run" });
    }

    /// <summary>Ignores a series for one service (stored as a mapping with an empty remote id).</summary>
    [HttpPost("ignore")]
    public async Task<IActionResult> Ignore([FromBody] IgnoreRequest request, CancellationToken ct)
    {
        await scrobbler.SaveMappingAsync(request.KavitaSeriesId, request.Service, "", "ignored", "", ct);
        return Ok(new { message = "ignored" });
    }

    // ---- per-tracker preferences ----

    /// <summary>Sets the per-tracker "scrobble reading" / "sync ratings" toggles.</summary>
    [HttpPut("preferences/{service}")]
    public async Task<IActionResult> SetPreferences(
        string service, [FromBody] PreferencesRequest request, CancellationToken ct)
    {
        if (scrobbler.FindTracker(service) is null)
        {
            return BadRequest(new { error = "unknown service" });
        }

        await settings.SetAsync(SettingKeys.ScrobbleReadingKey(service), request.Reading ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.ScrobbleRatingsKey(service), request.Ratings ? "true" : "false", ct);
        return Ok(new { service, reading = request.Reading, ratings = request.Ratings });
    }

    // ---- rating import (preview → apply) ----

    /// <summary>Starts a background preview of the ratings held on the given service.</summary>
    [HttpPost("import-ratings/{service}/preview")]
    public IActionResult StartRatingImport(string service)
    {
        if (scrobbler.FindTracker(service) is null)
        {
            return BadRequest(new { error = "unknown service" });
        }

        scrobbler.QueueRatingImportPreview(service);
        return Ok(new { started = true });
    }

    /// <summary>Polls the in-flight/last preview for a service.</summary>
    [HttpGet("import-ratings/{service}")]
    public IActionResult GetRatingImport(string service)
    {
        var state = scrobbler.GetRatingImport(service);
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
        var applied = await scrobbler.ApplyRatingImportAsync(service, request.SeriesIds, ct);
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
                    return BadRequest(new { error = "Set the AniList client id/secret in Settings first" });
                }

                {
                    var session = scrobbler.StartOAuthSession(service, redirectUri);
                    return Ok(new { url = await scrobbler.AniList.AuthorizeUrlAsync(redirectUri, session.State, ct) });
                }

            case "mal":
                if (!await scrobbler.Mal.ConfiguredAsync(ct))
                {
                    return BadRequest(new { error = "Set the MyAnimeList client id/secret in Settings first" });
                }

                {
                    var session = scrobbler.StartOAuthSession(service, redirectUri);
                    return Ok(new
                    {
                        url = await scrobbler.Mal.AuthorizeUrlAsync(redirectUri, session.State, session.CodeVerifier, ct),
                    });
                }

            default:
                return BadRequest(new { error = "unknown service" });
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
    /// OAuth redirect target. Anonymous (the provider redirects the user's browser
    /// here without an API key — exempted in ApiKeyMiddleware); the random state
    /// bound to the in-flight session authenticates the request instead.
    /// </summary>
    [HttpGet("/api/v1/scrobble/oauth/{service}")]
    public async Task<IActionResult> OAuthCallback(
        string service, [FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Redirect("/scrobble?error=" + Uri.EscapeDataString("OAuth was cancelled or returned no code"));
        }

        var session = scrobbler.TakeOAuthSession(service, state);
        if (session is null)
        {
            return Redirect("/scrobble?error=" + Uri.EscapeDataString(
                "OAuth state mismatch or session expired — retry the connection"));
        }

        try
        {
            switch (service)
            {
                case "anilist":
                    await scrobbler.AniList.ExchangeCodeAsync(code, session.RedirectUri, ct);
                    break;
                case "mal":
                    await scrobbler.Mal.ExchangeCodeAsync(code, session.CodeVerifier, session.RedirectUri, ct);
                    break;
                default:
                    return Redirect("/scrobble?error=" + Uri.EscapeDataString("unknown service"));
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
        if (service is not ("anilist" or "mal"))
        {
            return BadRequest(new { error = "unknown service" });
        }

        await tokens.DeleteAsync(service, ct);
        return Ok(new { message = "disconnected" });
    }
}
