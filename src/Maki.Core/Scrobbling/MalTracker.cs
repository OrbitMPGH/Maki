using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Scrobbling;

/// <summary>
/// MyAnimeList tracker (API v2, OAuth2 with PKCE-plain). Access tokens last ~31 days
/// and are refreshed automatically with the stored refresh token.
/// </summary>
public class MalTracker(
    IHttpClientFactory httpClientFactory,
    IAppSettings settings,
    IScrobbleTokenStore tokens,
    ScrobbleTrackerOptions options,
    ILogger<MalTracker> logger) : IScrobbleTracker, IAnimeListSource
{
    public const string HttpClientName = "scrobble";

    public string Name => "mal";
    public string Label => "MyAnimeList";
    public bool UsesOAuth => true;

    private static readonly Dictionary<string, ScrobbleStatus> StatusToInternal = new()
    {
        ["reading"] = ScrobbleStatus.Reading,
        ["completed"] = ScrobbleStatus.Completed,
        ["plan_to_read"] = ScrobbleStatus.PlanToRead,
    };

    private static readonly Dictionary<ScrobbleStatus, string> InternalToStatus = new()
    {
        [ScrobbleStatus.Reading] = "reading",
        [ScrobbleStatus.Completed] = "completed",
        [ScrobbleStatus.PlanToRead] = "plan_to_read",
    };

    public async Task<bool> ConfiguredAsync(CancellationToken ct = default) =>
        (await ClientIdAsync(ct)).Length > 0 && (await ClientSecretAsync(ct)).Length > 0;

    // Trim on read so a stray space/newline pasted into the credential can't silently
    // break auth — MAL then rejects the client with a Basic-auth popup + invalid_client.
    private async Task<string> ClientIdAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleMalClientId, ct))?.Trim() ?? "";

    private async Task<string> ClientSecretAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleMalClientSecret, ct))?.Trim() ?? "";

    public async Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) =>
        await tokens.GetAsync(userId, Name, ct) is not null;

    public async Task<string?> UsernameAsync(int userId, CancellationToken ct = default) =>
        (await tokens.GetAsync(userId, Name, ct))?.Username;

    // ---- OAuth (PKCE, 'plain' method: verifier == challenge) ----

    public async Task<string> AuthorizeUrlAsync(
        string redirectUri, string state, string codeVerifier, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync(ct);
        return $"{options.MalOAuthUrl}/authorize?response_type=code" +
               $"&client_id={Uri.EscapeDataString(clientId)}" +
               $"&code_challenge={Uri.EscapeDataString(codeVerifier)}&code_challenge_method=plain" +
               $"&state={Uri.EscapeDataString(state)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
    }

    public async Task ExchangeCodeAsync(
        int userId, string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var body = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = await ClientIdAsync(ct),
            ["client_secret"] = await ClientSecretAsync(ct),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        }, "token exchange", ct);
        await StoreTokenAsync(userId, body, ct);

        var me = await RequestAsync(userId, HttpMethod.Get, "/users/@me", null, ct);
        var token = await tokens.GetAsync(userId, Name, ct);
        if (token is not null)
        {
            token.Username = me.TryGetProperty("name", out var name) ? name.GetString() : null;
            await tokens.SaveAsync(token, ct);
        }
    }

    private async Task<JsonDocument> PostTokenAsync(
        Dictionary<string, string> form, string what, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{options.MalOAuthUrl}/token", new FormUrlEncodedContent(form), ct);
        }
        catch (HttpRequestException e)
        {
            throw new TrackerException($"MAL {what} request failed: {e.Message}", e);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException($"MAL {what} failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        return JsonDocument.Parse(body);
    }

    private async Task StoreTokenAsync(int userId, JsonDocument body, CancellationToken ct)
    {
        var existing = await tokens.GetAsync(userId, Name, ct);
        var expiresIn = body.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetDouble() : 2678400;
        await tokens.SaveAsync(new ScrobbleToken
        {
            UserId = userId,
            Service = Name,
            AccessToken = body.RootElement.GetProperty("access_token").GetString()
                          ?? throw new TrackerException("MAL returned no access token"),
            RefreshToken = body.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
            Username = existing?.Username,
        }, ct);
    }

    private async Task RefreshAsync(int userId, CancellationToken ct)
    {
        var token = await tokens.GetAsync(userId, Name, ct);
        if (token?.RefreshToken is null)
        {
            throw new TrackerException("MAL is not connected");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{options.MalOAuthUrl}/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = await ClientIdAsync(ct),
                    ["client_secret"] = await ClientSecretAsync(ct),
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken,
                }), ct);
        }
        catch (HttpRequestException e)
        {
            // network hiccup: keep the stored token, just fail this attempt
            throw new TrackerException($"MAL token refresh request failed: {e.Message}", e);
        }

        if (!response.IsSuccessStatusCode)
        {
            await tokens.DeleteAsync(userId, Name, ct);
            throw new TrackerException(
                $"MAL token refresh failed ({(int)response.StatusCode}) — reconnect the account");
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        await StoreTokenAsync(userId, body, ct);
    }

    // ---- API ----

    private async Task<JsonElement> RequestAsync(
        int userId, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var token = await tokens.GetAsync(userId, Name, ct)
                    ?? throw new TrackerException("MAL is not connected");
        if (token.ExpiresAt is { } expires && expires < DateTime.UtcNow.AddHours(1))
        {
            await RefreshAsync(userId, ct);
            token = await tokens.GetAsync(userId, Name, ct) ?? throw new TrackerException("MAL is not connected");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                var message = new HttpRequestMessage(method, $"{options.MalApiUrl}{path}") { Content = content };
                message.Headers.Authorization = new("Bearer", token.AccessToken);
                response = await client.SendAsync(message, ct);
            }
            catch (HttpRequestException e)
            {
                if (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                throw new TrackerException($"MAL request failed: {e.Message}", e);
            }

            try
            {
                if ((int)response.StatusCode == 401 && attempt == 0)
                {
                    await RefreshAsync(userId, ct);
                    token = await tokens.GetAsync(userId, Name, ct) ?? throw new TrackerException("MAL is not connected");
                    continue;
                }

                if ((int)response.StatusCode == 429)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                if ((int)response.StatusCode == 404)
                {
                    throw new TrackerEntryNotFoundException($"MAL {method} {path} not found (404)");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TrackerException(
                        $"MAL API {method} {path} failed ({(int)response.StatusCode}): {Truncate(body)}");
                }

                return JsonDocument.Parse(body).RootElement.Clone();
            }
            finally
            {
                response.Dispose();
            }
        }

        throw new TrackerException($"MAL API {method} {path} failed after retry");
    }

    public async Task<RemoteEntry> GetEntryAsync(
        int userId, string remoteId, CancellationToken ct = default)
    {
        var data = await RequestAsync(userId, HttpMethod.Get,
            $"/manga/{remoteId}?fields=title,num_chapters,num_volumes," +
            "my_list_status{status,num_chapters_read,num_volumes_read,score}", null, ct);
        var hasStatus = data.TryGetProperty("my_list_status", out var ls) && ls.ValueKind == JsonValueKind.Object;
        return new RemoteEntry(
            ProgressChapter: hasStatus ? GetInt(ls, "num_chapters_read") ?? 0 : 0,
            ProgressVolume: hasStatus ? GetInt(ls, "num_volumes_read") ?? 0 : 0,
            Status: hasStatus
                ? StatusToInternal.GetValueOrDefault(GetString(ls, "status") ?? "", ScrobbleStatus.Other)
                : null,
            // MAL reports 0 for unknown totals — treat as "unknown" like the original.
            TotalChapters: PositiveOrNull(GetInt(data, "num_chapters")),
            TotalVolumes: PositiveOrNull(GetInt(data, "num_volumes")),
            Title: GetString(data, "title") ?? "",
            // MAL's score is already 0–10; 0 means unrated.
            Score: hasStatus ? PositiveOrNull(GetInt(ls, "score")) : null);
    }

    public async Task UpdateAsync(
        int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["status"] = InternalToStatus[status],
            ["num_chapters_read"] = chapter.ToString(),
        };
        if (volume > 0)
        {
            form["num_volumes_read"] = volume.ToString();
        }

        await RequestAsync(userId, HttpMethod.Put, $"/manga/{remoteId}/my_list_status",
            new FormUrlEncodedContent(form), ct);
    }

    public async Task UpdateRatingAsync(
        int userId, string remoteId, int score, CancellationToken ct = default)
    {
        // MAL's score is 0–10, matching our internal scale; a score-only update leaves the rest of
        // the list entry untouched and adds the series to the list if it isn't there. 0 clears it.
        var clamped = Math.Clamp(score, 0, 10);
        await RequestAsync(userId, HttpMethod.Put, $"/manga/{remoteId}/my_list_status",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["score"] = clamped.ToString() }), ct);
    }

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(
        int userId, string title, CancellationToken ct = default)
    {
        var q = title.Length > 64 ? title[..64].Trim() : title.Trim();
        if (q.Length < 3)
        {
            return [];
        }

        var data = await RequestAsync(userId, HttpMethod.Get,
            $"/manga?q={Uri.EscapeDataString(q)}&limit=6&fields=alternative_titles", null, ct);
        var results = new List<ScrobbleCandidate>();
        if (data.TryGetProperty("data", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var names = new List<string?>();
                if (node.TryGetProperty("alternative_titles", out var alt) && alt.ValueKind == JsonValueKind.Object)
                {
                    names.AddRange([GetString(alt, "en"), GetString(alt, "ja")]);
                    if (alt.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                    {
                        names.AddRange(synonyms.EnumerateArray().Select(s => s.GetString()));
                    }
                }

                var id = node.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
                results.Add(new ScrobbleCandidate(
                    id, GetString(node, "title") ?? "",
                    names.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList(),
                    $"https://myanimelist.net/manga/{id}"));
            }
        }

        return results;
    }

    private static IEnumerable<string> RemoteStatusesFor(ScrobbleStatus status) => status switch
    {
        ScrobbleStatus.Reading => ["reading"],
        ScrobbleStatus.Completed => ["completed"],
        ScrobbleStatus.PlanToRead => ["plan_to_read"],
        _ => ["on_hold", "dropped"],
    };

    /// <summary>
    /// One pass per MAL status, since the list endpoint filters on a single status. Paged by offset
    /// for the same reason as <see cref="ListAnimeAsync"/>: <c>paging.next</c> is an absolute URL.
    /// </summary>
    public async Task<IReadOnlyList<RemoteListEntry>> ListAsync(
        int userId, IReadOnlyCollection<ScrobbleStatus> statuses, CancellationToken ct = default)
    {
        const int pageSize = 1000;
        var entries = new List<RemoteListEntry>();
        var seen = new HashSet<long>();
        const int maxOffset = 50_000;
        foreach (var remoteStatus in statuses.SelectMany(RemoteStatusesFor).Distinct())
        {
            var truncated = false;
            for (var offset = 0; offset < maxOffset; offset += pageSize)
            {
                var data = await RequestAsync(userId, HttpMethod.Get,
                    $"/users/@me/mangalist?status={remoteStatus}&fields=list_status&nsfw=true" +
                    $"&limit={pageSize}&offset={offset}", null, ct);
                if (data.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (!row.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object ||
                            GetInt(node, "id") is not { } mangaId || !seen.Add(mangaId))
                        {
                            continue;
                        }

                        var listStatus = row.TryGetProperty("list_status", out var ls) && ls.ValueKind == JsonValueKind.Object
                            ? GetString(ls, "status")
                            : null;
                        var status = StatusToInternal.GetValueOrDefault(listStatus ?? remoteStatus, ScrobbleStatus.Other);
                        if (!statuses.Contains(status))
                        {
                            continue;
                        }

                        entries.Add(new RemoteListEntry(
                            mangaId.ToString(CultureInfo.InvariantCulture), status, GetString(node, "title") ?? "", MalId: mangaId));
                    }
                }

                var hasNext = data.TryGetProperty("paging", out var paging) && paging.ValueKind == JsonValueKind.Object &&
                              GetString(paging, "next") is not null;
                truncated = hasNext && offset + pageSize >= maxOffset;
                if (!hasNext)
                {
                    break;
                }
            }

            if (truncated)
            {
                logger.LogWarning(
                    "MAL {Status} list for user {UserId} stopped at the {Max}-entry cap ({Count} entries); " +
                    "the rest of the list was not read",
                    remoteStatus, userId, maxOffset, entries.Count);
            }
        }

        return entries;
    }

    public string EntryUrl(string remoteId) => $"https://myanimelist.net/manga/{remoteId}";

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ---- anime list ----

    private static readonly Dictionary<string, AnimeWatchStatus> AnimeStatusToInternal = new()
    {
        ["watching"] = AnimeWatchStatus.Watching,
        ["completed"] = AnimeWatchStatus.Completed,
        ["on_hold"] = AnimeWatchStatus.OnHold,
        ["dropped"] = AnimeWatchStatus.Dropped,
        ["plan_to_watch"] = AnimeWatchStatus.Planning,
    };

    /// <summary>
    /// The whole anime list, paged by offset rather than by following <c>paging.next</c>: that field
    /// is an absolute URL and everything else here goes through <see cref="RequestAsync"/>, which
    /// takes a path. Carries no relation data, so every entry comes back unresolved and the sync
    /// asks <see cref="RelatedMangaAsync"/> once per anime it has not asked about before.
    /// </summary>
    public async Task<IReadOnlyList<AnimeListEntry>> ListAnimeAsync(int userId, CancellationToken ct = default)
    {
        const int pageSize = 1000;
        var entries = new List<AnimeListEntry>();
        var seen = new HashSet<long>();
        const int maxOffset = 20_000;
        var truncated = false;
        for (var offset = 0; offset < maxOffset; offset += pageSize)
        {
            var data = await RequestAsync(userId, HttpMethod.Get,
                $"/users/@me/animelist?fields=list_status,media_type,num_episodes,start_date,end_date&nsfw=true&limit={pageSize}&offset={offset}", null, ct);
            if (!data.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var row in rows.EnumerateArray())
            {
                count++;
                if (!row.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object ||
                    GetInt(node, "id") is not { } animeId || !seen.Add(animeId))
                {
                    continue;
                }

                var hasStatus = row.TryGetProperty("list_status", out var ls) &&
                                ls.ValueKind == JsonValueKind.Object;
                entries.Add(new AnimeListEntry(
                    animeId,
                    GetString(node, "title") ?? string.Empty,
                    hasStatus ? PositiveOrNull(GetInt(ls, "score")) : null,
                    hasStatus
                        ? AnimeStatusToInternal.GetValueOrDefault(
                            GetString(ls, "status") ?? string.Empty, AnimeWatchStatus.Planning)
                        : AnimeWatchStatus.Planning,
                    MalAnimeId: animeId,
                    Format: AnimeListFields.NormalizeFormat(GetString(node, "media_type")),
                    StartDate: AnimeListFields.ParseFullDate(GetString(node, "start_date")),
                    EndDate: AnimeListFields.ParseFullDate(GetString(node, "end_date")),
                    Episodes: PositiveOrNull(GetInt(node, "num_episodes")),
                    Progress: hasStatus ? GetInt(ls, "num_episodes_watched") : null));
            }

            truncated = count >= pageSize && offset + pageSize >= maxOffset;
            if (count < pageSize)
            {
                break;
            }
        }

        if (truncated)
        {
            logger.LogWarning(
                "MAL anime list for user {UserId} stopped at the {Max}-entry cap ({Count} entries); " +
                "the rest of the list was not read",
                userId, maxOffset, entries.Count);
        }

        return entries;
    }

    /// <summary>
    /// MyAnimeList's v2 API has no anime-to-manga relation field at all: <c>fields=related_manga</c>
    /// on the anime endpoint always comes back an empty array, so this used to match nothing. Resolved
    /// through AniList's public GraphQL instead, unauthenticated, by this anime's MAL id
    /// (<c>Media(idMal: ...)</c>). AniList carries the adaptation relations MAL does not expose, and
    /// no AniList entry for that MAL id (a 404 with a GraphQL <c>errors</c> array, or a 2xx with a
    /// null <c>Media</c>) is a real "no match" and returns null rather than throwing.
    /// </summary>
    public async Task<AnimeRelatedManga?> RelatedMangaAsync(
        int userId, long animeId, CancellationToken ct = default)
    {
        const string query = """
            query($idMal:Int){ Media(idMal:$idMal, type:ANIME){
              relations { edges { relationType node { id idMal type format } } } } }
            """;

        var data = await QueryAniListAsync(query, new { idMal = (int)animeId }, ct);
        if (data is not { } d || !d.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var pick = AnimeRelationPicker.Pick(AnimeRelationPicker.ReadRelations(media));
        return pick is { } p ? new AnimeRelatedManga(p.Id, p.IdMal) : null;
    }

    /// <summary>
    /// One unauthenticated GraphQL call to AniList's public endpoint. Unauthenticated is limited to
    /// 90 requests/min and answers with 429 + <c>Retry-After</c> when exceeded; waited out once (capped
    /// at 60s) and then given up on, so one slow row cannot stall the rest of the pass.
    /// <para>
    /// A connection failure, a second 429, and every non-2xx other than "404 with a not-found
    /// <c>errors</c> array" are transport failures and throw <see cref="HttpRequestException"/>, which
    /// <c>AnimeSignalSyncService.IsTransportFailure</c> catches and leaves the row unstamped so the
    /// next pass retries it. A 2xx with an <c>errors</c> array that is not that not-found shape is
    /// ambiguous and is also treated as transport rather than as a real "no match".
    /// </para>
    /// </summary>
    private async Task<JsonElement?> QueryAniListAsync(string query, object variables, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var waited429 = false;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.PostAsJsonAsync(options.AniListApiUrl, new { query, variables }, ct);
            }
            catch (HttpRequestException e)
            {
                throw new HttpRequestException($"AniList relation lookup failed: {e.Message}", e);
            }

            if ((int)response.StatusCode == 429)
            {
                if (!waited429)
                {
                    waited429 = true;
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
                    continue;
                }

                throw new HttpRequestException(
                    $"AniList relation lookup failed ({(int)response.StatusCode}): rate limited again");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var hasErrors = HasErrors(body);

            if (response.StatusCode == HttpStatusCode.NotFound && hasErrors)
            {
                // AniList answers a 404 with a GraphQL errors array when no Media has this idMal.
                // That is a real "no match", not a transport problem.
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"AniList relation lookup failed ({(int)response.StatusCode}): {Truncate(body)}");
            }

            if (hasErrors)
            {
                // A 2xx with errors is ambiguous, not a confirmed not-found: treat as transport so it
                // retries instead of being stamped as "no manga relation".
                throw new HttpRequestException($"AniList relation lookup returned errors: {Truncate(body)}");
            }

            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("data", out var payload) ? payload.Clone() : null;
        }
    }

    private static bool HasErrors(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("errors", out var e) && e.ValueKind != JsonValueKind.Null;
        }
        catch (JsonException)
        {
            // A non-JSON body (a plain-text 500 page, say) carries no errors array to read.
            return false;
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
