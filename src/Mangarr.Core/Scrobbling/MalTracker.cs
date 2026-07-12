using System.Text.Json;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;

namespace Mangarr.Core.Scrobbling;

/// <summary>
/// MyAnimeList tracker (API v2, OAuth2 with PKCE-plain). Access tokens last ~31 days
/// and are refreshed automatically with the stored refresh token.
/// </summary>
public class MalTracker(
    IHttpClientFactory httpClientFactory,
    IAppSettings settings,
    IScrobbleTokenStore tokens,
    ScrobbleTrackerOptions options) : IScrobbleTracker
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
        !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.ScrobbleMalClientId, ct)) &&
        !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.ScrobbleMalClientSecret, ct));

    public async Task<bool> AuthenticatedAsync(CancellationToken ct = default) =>
        await tokens.GetAsync(Name, ct) is not null;

    public async Task<string?> UsernameAsync(CancellationToken ct = default) =>
        (await tokens.GetAsync(Name, ct))?.Username;

    // ---- OAuth (PKCE, 'plain' method: verifier == challenge) ----

    public async Task<string> AuthorizeUrlAsync(
        string redirectUri, string state, string codeVerifier, CancellationToken ct = default)
    {
        var clientId = await settings.GetAsync(SettingKeys.ScrobbleMalClientId, ct);
        return $"{options.MalOAuthUrl}/authorize?response_type=code" +
               $"&client_id={Uri.EscapeDataString(clientId ?? "")}" +
               $"&code_challenge={Uri.EscapeDataString(codeVerifier)}&code_challenge_method=plain" +
               $"&state={Uri.EscapeDataString(state)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
    }

    public async Task ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var body = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = await settings.GetAsync(SettingKeys.ScrobbleMalClientId, ct) ?? "",
            ["client_secret"] = await settings.GetAsync(SettingKeys.ScrobbleMalClientSecret, ct) ?? "",
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        }, "token exchange", ct);
        await StoreTokenAsync(body, ct);

        var me = await RequestAsync(HttpMethod.Get, "/users/@me", null, ct);
        var token = await tokens.GetAsync(Name, ct);
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

    private async Task StoreTokenAsync(JsonDocument body, CancellationToken ct)
    {
        var existing = await tokens.GetAsync(Name, ct);
        var expiresIn = body.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetDouble() : 2678400;
        await tokens.SaveAsync(new ScrobbleToken
        {
            Service = Name,
            AccessToken = body.RootElement.GetProperty("access_token").GetString()
                          ?? throw new TrackerException("MAL returned no access token"),
            RefreshToken = body.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
            Username = existing?.Username,
        }, ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var token = await tokens.GetAsync(Name, ct);
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
                    ["client_id"] = await settings.GetAsync(SettingKeys.ScrobbleMalClientId, ct) ?? "",
                    ["client_secret"] = await settings.GetAsync(SettingKeys.ScrobbleMalClientSecret, ct) ?? "",
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
            await tokens.DeleteAsync(Name, ct);
            throw new TrackerException(
                $"MAL token refresh failed ({(int)response.StatusCode}) — reconnect the account");
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        await StoreTokenAsync(body, ct);
    }

    // ---- API ----

    private async Task<JsonElement> RequestAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var token = await tokens.GetAsync(Name, ct)
                    ?? throw new TrackerException("MAL is not connected");
        if (token.ExpiresAt is { } expires && expires < DateTime.UtcNow.AddHours(1))
        {
            await RefreshAsync(ct);
            token = await tokens.GetAsync(Name, ct) ?? throw new TrackerException("MAL is not connected");
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

            if ((int)response.StatusCode == 401 && attempt == 0)
            {
                await RefreshAsync(ct);
                token = await tokens.GetAsync(Name, ct) ?? throw new TrackerException("MAL is not connected");
                continue;
            }

            if ((int)response.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new TrackerException(
                    $"MAL API {method} {path} failed ({(int)response.StatusCode}): {Truncate(body)}");
            }

            return JsonDocument.Parse(body).RootElement.Clone();
        }

        throw new TrackerException($"MAL API {method} {path} failed after retry");
    }

    public async Task<RemoteEntry> GetEntryAsync(string remoteId, CancellationToken ct = default)
    {
        var data = await RequestAsync(HttpMethod.Get,
            $"/manga/{remoteId}?fields=title,num_chapters,num_volumes," +
            "my_list_status{status,num_chapters_read,num_volumes_read}", null, ct);
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
            Title: GetString(data, "title") ?? "");
    }

    public async Task UpdateAsync(
        string remoteId, int chapter, int volume, ScrobbleStatus status, CancellationToken ct = default)
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

        await RequestAsync(HttpMethod.Put, $"/manga/{remoteId}/my_list_status",
            new FormUrlEncodedContent(form), ct);
    }

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(string title, CancellationToken ct = default)
    {
        var q = title.Length > 64 ? title[..64].Trim() : title.Trim();
        if (q.Length < 3)
        {
            return [];
        }

        var data = await RequestAsync(HttpMethod.Get,
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

                var id = node.GetProperty("id").GetInt32().ToString();
                results.Add(new ScrobbleCandidate(
                    id, GetString(node, "title") ?? "",
                    names.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList(),
                    $"https://myanimelist.net/manga/{id}"));
            }
        }

        return results;
    }

    public string EntryUrl(string remoteId) => $"https://myanimelist.net/manga/{remoteId}";

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
