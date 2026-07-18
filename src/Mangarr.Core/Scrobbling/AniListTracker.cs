using System.Net.Http.Json;
using System.Text.Json;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Mangarr.Core.Scrobbling;

/// <summary>
/// AniList tracker (GraphQL API, OAuth2 authorization-code flow). Tokens are valid
/// for ~1 year; AniList does not issue refresh tokens for the code flow, so the
/// user reconnects when it expires.
/// </summary>
public class AniListTracker(
    IHttpClientFactory httpClientFactory,
    IAppSettings settings,
    IScrobbleTokenStore tokens,
    ScrobbleTrackerOptions options,
    ILogger<AniListTracker> logger) : IScrobbleTracker
{
    public const string HttpClientName = "scrobble";

    public string Name => "anilist";
    public string Label => "AniList";
    public bool UsesOAuth => true;

    private static readonly Dictionary<string, ScrobbleStatus> StatusToInternal = new()
    {
        ["CURRENT"] = ScrobbleStatus.Reading,
        ["REPEATING"] = ScrobbleStatus.Reading,
        ["COMPLETED"] = ScrobbleStatus.Completed,
        ["PLANNING"] = ScrobbleStatus.PlanToRead,
    };

    private static readonly Dictionary<ScrobbleStatus, string> InternalToStatus = new()
    {
        [ScrobbleStatus.Reading] = "CURRENT",
        [ScrobbleStatus.Completed] = "COMPLETED",
        [ScrobbleStatus.PlanToRead] = "PLANNING",
    };

    public async Task<bool> ConfiguredAsync(CancellationToken ct = default) =>
        (await ClientIdAsync(ct)).Length > 0 && (await ClientSecretAsync(ct)).Length > 0;

    // Trim on read so a stray space/newline in a pasted credential can't silently break auth.
    private async Task<string> ClientIdAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleAniListClientId, ct))?.Trim() ?? "";

    private async Task<string> ClientSecretAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleAniListClientSecret, ct))?.Trim() ?? "";

    public async Task<bool> AuthenticatedAsync(CancellationToken ct = default)
    {
        var token = await tokens.GetAsync(Name, ct);
        return token is not null && token.AccessToken.Length > 0 &&
               (token.ExpiresAt is null || token.ExpiresAt > DateTime.UtcNow);
    }

    public async Task<string?> UsernameAsync(CancellationToken ct = default) =>
        (await tokens.GetAsync(Name, ct))?.Username;

    // ---- OAuth ----

    public async Task<string> AuthorizeUrlAsync(string redirectUri, string state, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync(ct);
        return $"{options.AniListOAuthUrl}/authorize?client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync($"{options.AniListOAuthUrl}/token", new
            {
                grant_type = "authorization_code",
                client_id = await ClientIdAsync(ct),
                client_secret = await ClientSecretAsync(ct),
                redirect_uri = redirectUri,
                code,
            }, ct);
        }
        catch (HttpRequestException e)
        {
            throw new TrackerException($"AniList token request failed: {e.Message}", e);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException($"AniList token exchange failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.GetProperty("access_token").GetString()
                          ?? throw new TrackerException("AniList token exchange returned no access token");
        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetDouble() : 31536000;
        var token = new ScrobbleToken
        {
            Service = Name,
            AccessToken = accessToken,
            RefreshToken = json.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
        };
        await tokens.SaveAsync(token, ct);

        var viewer = await QueryAsync("query { Viewer { id name } }", new { }, auth: true, ct);
        token.Username = viewer.GetProperty("Viewer").TryGetProperty("name", out var name) ? name.GetString() : null;
        await tokens.SaveAsync(token, ct);
    }

    // ---- API ----

    private async Task<JsonElement> QueryAsync(string query, object variables, bool auth, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var request = () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, options.AniListApiUrl)
            {
                Content = JsonContent.Create(new { query, variables }),
            };
            return message;
        };

        string? bearer = null;
        if (auth)
        {
            var token = await tokens.GetAsync(Name, ct);
            if (token is null || token.AccessToken.Length == 0)
            {
                throw new TrackerException("AniList is not connected");
            }

            bearer = token.AccessToken;
        }

        // Retried here rather than by TransientRetryHandler, which only covers GET/HEAD: AniList is
        // GraphQL-over-POST, and replaying a POST blind is normally unsafe. It's safe in this one
        // case because every call is either a read or SaveMediaListEntry, which sets progress to an
        // absolute value — running it twice lands on the same state.
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var lastAttempt = attempt == maxAttempts;
            HttpResponseMessage response;
            try
            {
                var message = request();
                if (bearer is not null)
                {
                    message.Headers.Authorization = new("Bearer", bearer);
                }

                response = await client.SendAsync(message, ct);
            }
            catch (HttpRequestException e)
            {
                if (!lastAttempt)
                {
                    await Task.Delay(BackoffFor(attempt), ct);
                    continue;
                }

                throw new TrackerException($"AniList request failed: {e.Message}", e);
            }

            if ((int)response.StatusCode == 429)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                logger.LogWarning("AniList rate limited, waiting {Wait}s", wait.TotalSeconds);
                await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
                continue;
            }

            // A 5xx is AniList having a moment, not a bad request — worth another go.
            if ((int)response.StatusCode >= 500 && !lastAttempt)
            {
                logger.LogWarning(
                    "AniList returned {Status}; retrying (attempt {Attempt}/{Max})",
                    (int)response.StatusCode, attempt, maxAttempts);
                response.Dispose();
                await Task.Delay(BackoffFor(attempt), ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode ||
                (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null))
            {
                throw new TrackerException(
                    $"AniList API error ({(int)response.StatusCode}): {Truncate(json.RootElement.TryGetProperty("errors", out var e2) ? e2.GetRawText() : body)}");
            }

            return json.RootElement.GetProperty("data").Clone();
        }

        throw new TrackerException("AniList API rate limit persisted after retry");
    }

    /// <summary>Exponential backoff with jitter, so a fleet of scrobbles doesn't retry in lockstep.</summary>
    private static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromSeconds(2 * Math.Pow(2, attempt - 1) * (Random.Shared.NextDouble() * 0.3 + 0.85));

    public async Task<RemoteEntry> GetEntryAsync(string remoteId, CancellationToken ct = default)
    {
        var data = await QueryAsync(
            """
            query($id:Int){ Media(id:$id, type:MANGA){
              chapters volumes title{ romaji english }
              mediaListEntry{ status progress progressVolumes } } }
            """,
            new { id = int.Parse(remoteId) }, auth: true, ct);
        if (data.TryGetProperty("Media", out var media) is false || media.ValueKind == JsonValueKind.Null)
        {
            throw new TrackerException($"AniList media {remoteId} not found");
        }

        var hasEntry = media.TryGetProperty("mediaListEntry", out var entry) && entry.ValueKind == JsonValueKind.Object;
        var titles = media.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        return new RemoteEntry(
            ProgressChapter: hasEntry ? GetInt(entry, "progress") ?? 0 : 0,
            ProgressVolume: hasEntry ? GetInt(entry, "progressVolumes") ?? 0 : 0,
            Status: hasEntry
                ? StatusToInternal.GetValueOrDefault(GetString(entry, "status") ?? "", ScrobbleStatus.Other)
                : null,
            TotalChapters: GetInt(media, "chapters"),
            TotalVolumes: GetInt(media, "volumes"),
            Title: (titles.ValueKind == JsonValueKind.Object
                       ? GetString(titles, "english") ?? GetString(titles, "romaji")
                       : null) ?? "");
    }

    public async Task UpdateAsync(
        string remoteId, int chapter, int volume, ScrobbleStatus status, CancellationToken ct = default)
    {
        object variables = volume > 0
            ? new { mediaId = int.Parse(remoteId), status = InternalToStatus[status], progress = chapter, progressVolumes = volume }
            : new { mediaId = int.Parse(remoteId), status = InternalToStatus[status], progress = chapter };
        await QueryAsync(
            """
            mutation($mediaId:Int,$status:MediaListStatus,$progress:Int,$progressVolumes:Int){
              SaveMediaListEntry(mediaId:$mediaId,status:$status,progress:$progress,
                                 progressVolumes:$progressVolumes){ id } }
            """,
            variables, auth: true, ct);
    }

    public async Task UpdateRatingAsync(string remoteId, int score, CancellationToken ct = default)
    {
        // scoreRaw is always on AniList's 100-point scale regardless of the user's display format,
        // so our 1–10 maps to 0–100 by *10. Creates the list entry if one doesn't exist yet.
        var raw = Math.Clamp(score, 0, 10) * 10;
        await QueryAsync(
            "mutation($mediaId:Int,$scoreRaw:Int){ SaveMediaListEntry(mediaId:$mediaId,scoreRaw:$scoreRaw){ id } }",
            new { mediaId = int.Parse(remoteId), scoreRaw = raw }, auth: true, ct);
    }

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(string title, CancellationToken ct = default)
    {
        var data = await QueryAsync(
            """
            query($q:String){ Page(perPage:6){
              media(search:$q, type:MANGA){
                id idMal title{ romaji english native } synonyms } } }
            """,
            new { q = title }, auth: false, ct);
        var results = new List<ScrobbleCandidate>();
        if (data.TryGetProperty("Page", out var page) && page.TryGetProperty("media", out var media) &&
            media.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in media.EnumerateArray())
            {
                var titles = m.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
                var names = new List<string?>();
                if (titles.ValueKind == JsonValueKind.Object)
                {
                    names.AddRange([GetString(titles, "english"), GetString(titles, "romaji"), GetString(titles, "native")]);
                }

                if (m.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                {
                    names.AddRange(synonyms.EnumerateArray().Select(s => s.GetString()));
                }

                var valid = names.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
                if (valid.Count == 0)
                {
                    continue;
                }

                var id = m.GetProperty("id").GetInt32().ToString();
                results.Add(new ScrobbleCandidate(id, valid[0], valid.Skip(1).ToList(), $"https://anilist.co/manga/{id}"));
            }
        }

        return results;
    }

    /// <summary>AniList knows the MAL id for most entries — free cross-mapping.</summary>
    public async Task<string?> GetMalIdAsync(string anilistId, CancellationToken ct = default)
    {
        try
        {
            var data = await QueryAsync("query($id:Int){ Media(id:$id, type:MANGA){ idMal } }",
                new { id = int.Parse(anilistId) }, auth: false, ct);
            return data.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Object
                ? GetInt(media, "idMal")?.ToString()
                : null;
        }
        catch (TrackerException)
        {
            return null;
        }
    }

    public string EntryUrl(string remoteId) => $"https://anilist.co/manga/{remoteId}";

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
