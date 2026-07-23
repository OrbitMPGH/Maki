using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Scrobbling;

/// <summary>
/// Kitsu tracker (JSON:API, OAuth2 resource-owner password grant — Kitsu has no
/// authorization-code redirect flow, so the user's email/password are exchanged
/// for a token directly rather than via a Connect button). <see cref="UsesOAuth"/>
/// is false: there is nothing for the frontend to redirect to, and login happens
/// lazily the first time the tracker is asked whether it's authenticated.
/// </summary>
public class KitsuTracker(
    IHttpClientFactory httpClientFactory,
    IAppSettings settings,
    IScrobbleTokenStore tokens,
    ScrobbleTrackerOptions options,
    ILogger<KitsuTracker> logger) : IScrobbleTracker
{
    public const string HttpClientName = "scrobble";
    private static readonly MediaTypeHeaderValue JsonApiContentType = new("application/vnd.api+json");
    private static readonly MediaTypeWithQualityHeaderValue JsonApiAcceptType = new("application/vnd.api+json");

    public string Name => "kitsu";
    public string Label => "Kitsu";
    public bool UsesOAuth => false;

    private static readonly Dictionary<string, ScrobbleStatus> StatusToInternal = new()
    {
        ["current"] = ScrobbleStatus.Reading,
        ["completed"] = ScrobbleStatus.Completed,
        ["planned"] = ScrobbleStatus.PlanToRead,
    };

    private static readonly Dictionary<ScrobbleStatus, string> InternalToStatus = new()
    {
        [ScrobbleStatus.Reading] = "current",
        [ScrobbleStatus.Completed] = "completed",
        [ScrobbleStatus.PlanToRead] = "planned",
    };

    /// <summary>Numeric Kitsu user id, resolved once per process from the access token.</summary>
    private string? _cachedUserId;

    /// <summary>Avoids hammering the login endpoint every status poll when credentials are bad.</summary>
    private (DateTime CheckedAt, bool Ok)? _authCache;

    public async Task<bool> ConfiguredAsync(CancellationToken ct = default) =>
        (await ClientIdAsync(ct)).Length > 0 && (await ClientSecretAsync(ct)).Length > 0 &&
        (await EmailAsync(ct)).Length > 0 && (await PasswordAsync(ct)).Length > 0;

    private async Task<string> ClientIdAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleKitsuClientId, ct))?.Trim() ?? "";

    private async Task<string> ClientSecretAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleKitsuClientSecret, ct))?.Trim() ?? "";

    private async Task<string> EmailAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleKitsuEmail, ct))?.Trim() ?? "";

    private async Task<string> PasswordAsync(CancellationToken ct) =>
        await settings.GetAsync(SettingKeys.ScrobbleKitsuPassword, ct) ?? "";

    /// <summary>
    /// True when a usable token exists or one can be obtained now. Doubles as the "log
    /// in" trigger since Kitsu has no redirect flow to do that from — a failed attempt
    /// is cached for a few minutes so bad credentials don't retry on every status poll.
    /// </summary>
    public async Task<bool> AuthenticatedAsync(CancellationToken ct = default)
    {
        if (!await ConfiguredAsync(ct))
        {
            return false;
        }

        var token = await tokens.GetAsync(Name, ct);
        if (token is not null && token.AccessToken.Length > 0 &&
            (token.ExpiresAt is null || token.ExpiresAt > DateTime.UtcNow.AddMinutes(5)))
        {
            return true;
        }

        if (token?.RefreshToken is not null)
        {
            try
            {
                await RefreshAsync(token.RefreshToken, ct);
                return true;
            }
            catch (TrackerException e)
            {
                logger.LogInformation("Kitsu token refresh failed, will re-login: {Error}", e.Message);
            }
        }

        if (_authCache is { } cached && DateTime.UtcNow - cached.CheckedAt < TimeSpan.FromMinutes(5))
        {
            return cached.Ok;
        }

        var ok = false;
        try
        {
            await LoginAsync(ct);
            ok = true;
        }
        catch (TrackerException e)
        {
            logger.LogWarning("Kitsu login failed: {Error}", e.Message);
        }

        _authCache = (DateTime.UtcNow, ok);
        return ok;
    }

    public async Task<string?> UsernameAsync(CancellationToken ct = default) =>
        (await tokens.GetAsync(Name, ct))?.Username;

    // ---- auth ----

    private async Task LoginAsync(CancellationToken ct)
    {
        var body = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = await EmailAsync(ct),
            ["password"] = await PasswordAsync(ct),
            ["client_id"] = await ClientIdAsync(ct),
            ["client_secret"] = await ClientSecretAsync(ct),
        }, "login", ct);
        await StoreTokenAsync(body, ct);
        _cachedUserId = null;
        await FetchProfileAsync(ct);
    }

    private async Task RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var body = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = await ClientIdAsync(ct),
            ["client_secret"] = await ClientSecretAsync(ct),
        }, "token refresh", ct);
        await StoreTokenAsync(body, ct);
    }

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, string what, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{options.KitsuOAuthUrl}/token", new FormUrlEncodedContent(form), ct);
        }
        catch (HttpRequestException e)
        {
            throw new TrackerException($"Kitsu {what} request failed: {e.Message}", e);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException($"Kitsu {what} failed ({(int)response.StatusCode}): {Truncate(responseBody)}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private async Task StoreTokenAsync(JsonDocument body, CancellationToken ct)
    {
        var existing = await tokens.GetAsync(Name, ct);
        var expiresIn = body.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetDouble() : 2591940;
        await tokens.SaveAsync(new ScrobbleToken
        {
            Service = Name,
            AccessToken = body.RootElement.GetProperty("access_token").GetString()
                          ?? throw new TrackerException("Kitsu returned no access token"),
            RefreshToken = body.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
            Username = existing?.Username,
        }, ct);
    }

    private async Task<string> FetchProfileAsync(CancellationToken ct)
    {
        var data = await RequestAsync(HttpMethod.Get, "/users?filter[self]=true&fields[users]=name", auth: true, ct: ct);
        var user = data.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0]
            : throw new TrackerException("Kitsu profile lookup returned no user");
        var id = user.GetProperty("id").GetString() ?? throw new TrackerException("Kitsu profile has no id");
        var name = user.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? GetString(attrs, "name")
            : null;

        _cachedUserId = id;
        var token = await tokens.GetAsync(Name, ct);
        if (token is not null)
        {
            token.Username = name;
            await tokens.SaveAsync(token, ct);
        }

        return id;
    }

    private async Task<string> UserIdAsync(CancellationToken ct) =>
        _cachedUserId ?? await FetchProfileAsync(ct);

    // ---- API ----

    private async Task<JsonElement> RequestAsync(
        HttpMethod method, string path, bool auth = false, JsonObject? jsonBody = null, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string? bearer = null;
            if (auth)
            {
                var token = await tokens.GetAsync(Name, ct) ?? throw new TrackerException("Kitsu is not connected");
                bearer = token.AccessToken;
            }

            HttpResponseMessage response;
            try
            {
                var message = new HttpRequestMessage(method, $"{options.KitsuApiUrl}{path}");
                message.Headers.Accept.Add(JsonApiAcceptType);
                if (bearer is not null)
                {
                    message.Headers.Authorization = new("Bearer", bearer);
                }

                if (jsonBody is not null)
                {
                    message.Content = new StringContent(jsonBody.ToJsonString());
                    message.Content.Headers.ContentType = JsonApiContentType;
                }

                response = await client.SendAsync(message, ct);
            }
            catch (HttpRequestException e)
            {
                if (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                throw new TrackerException($"Kitsu request failed: {e.Message}", e);
            }

            if ((int)response.StatusCode == 401 && auth && attempt == 0)
            {
                var token = await tokens.GetAsync(Name, ct);
                if (token?.RefreshToken is not null)
                {
                    await RefreshAsync(token.RefreshToken, ct);
                    continue;
                }

                await LoginAsync(ct);
                continue;
            }

            if ((int)response.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode == 404)
            {
                throw new TrackerEntryNotFoundException($"Kitsu {method} {path} not found (404)");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TrackerException($"Kitsu API {method} {path} failed ({(int)response.StatusCode}): {Truncate(responseBody)}");
            }

            return responseBody.Length == 0 ? default : JsonDocument.Parse(responseBody).RootElement.Clone();
        }

        throw new TrackerException($"Kitsu API {method} {path} failed after retry");
    }

    public async Task<RemoteEntry> GetEntryAsync(string remoteId, CancellationToken ct = default)
    {
        var manga = await RequestAsync(HttpMethod.Get,
            $"/manga/{remoteId}?fields[manga]=canonicalTitle,titles,chapterCount,volumeCount", ct: ct);
        var attrs = manga.TryGetProperty("data", out var md) && md.ValueKind == JsonValueKind.Object &&
                    md.TryGetProperty("attributes", out var a) ? a : default;

        var userId = await UserIdAsync(ct);
        var lib = await RequestAsync(HttpMethod.Get,
            $"/library-entries?filter[userId]={userId}&filter[kind]=manga&filter[mangaId]={remoteId}" +
            "&fields[libraryEntries]=status,progress,ratingTwenty", auth: true, ct: ct);
        var hasEntry = lib.TryGetProperty("data", out var entries) && entries.ValueKind == JsonValueKind.Array &&
                       entries.GetArrayLength() > 0;
        var entryAttrs = hasEntry && entries[0].TryGetProperty("attributes", out var ea) ? ea : default;

        return new RemoteEntry(
            ProgressChapter: hasEntry ? GetInt(entryAttrs, "progress") ?? 0 : 0,
            ProgressVolume: 0, // Kitsu library entries don't track volume progress separately.
            Status: hasEntry
                ? StatusToInternal.GetValueOrDefault(GetString(entryAttrs, "status") ?? "", ScrobbleStatus.Other)
                : null,
            TotalChapters: attrs.ValueKind == JsonValueKind.Object ? PositiveOrNull(GetInt(attrs, "chapterCount")) : null,
            TotalVolumes: attrs.ValueKind == JsonValueKind.Object ? PositiveOrNull(GetInt(attrs, "volumeCount")) : null,
            Title: attrs.ValueKind == JsonValueKind.Object ? GetString(attrs, "canonicalTitle") ?? "" : "",
            // ratingTwenty is 2-20 in half-point steps; our internal scale is 1-10.
            Score: hasEntry && GetInt(entryAttrs, "ratingTwenty") is > 0 and { } rt
                ? Math.Clamp((int)Math.Round(rt / 2.0), 1, 10)
                : null);
    }

    public async Task UpdateAsync(
        string remoteId, int chapter, int volume, ScrobbleStatus status, CancellationToken ct = default)
    {
        var attributes = new JsonObject
        {
            ["status"] = InternalToStatus[status],
            ["progress"] = chapter,
        };
        await UpsertLibraryEntryAsync(remoteId, attributes, ct);
    }

    public async Task UpdateRatingAsync(string remoteId, int score, CancellationToken ct = default)
    {
        // 0 clears the rating; otherwise our 1-10 maps to Kitsu's 2-20 half-point scale.
        var attributes = new JsonObject
        {
            ["ratingTwenty"] = score <= 0 ? null : Math.Clamp(score, 1, 10) * 2,
        };
        await UpsertLibraryEntryAsync(remoteId, attributes, ct);
    }

    private async Task UpsertLibraryEntryAsync(string mangaId, JsonObject attributes, CancellationToken ct)
    {
        var userId = await UserIdAsync(ct);
        var existingId = await FindLibraryEntryIdAsync(mangaId, userId, ct);

        if (existingId is not null)
        {
            var body = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["id"] = existingId,
                    ["type"] = "libraryEntries",
                    ["attributes"] = attributes,
                },
            };
            await RequestAsync(HttpMethod.Patch, $"/library-entries/{existingId}", auth: true, jsonBody: body, ct: ct);
            return;
        }

        var createBody = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "libraryEntries",
                ["attributes"] = attributes,
                ["relationships"] = new JsonObject
                {
                    ["user"] = new JsonObject
                    {
                        ["data"] = new JsonObject { ["id"] = userId, ["type"] = "users" },
                    },
                    ["media"] = new JsonObject
                    {
                        ["data"] = new JsonObject { ["id"] = mangaId, ["type"] = "manga" },
                    },
                },
            },
        };
        await RequestAsync(HttpMethod.Post, "/library-entries", auth: true, jsonBody: createBody, ct: ct);
    }

    private async Task<string?> FindLibraryEntryIdAsync(string mangaId, string userId, CancellationToken ct)
    {
        var data = await RequestAsync(HttpMethod.Get,
            $"/library-entries?filter[userId]={userId}&filter[kind]=manga&filter[mangaId]={mangaId}" +
            "&fields[libraryEntries]=id", auth: true, ct: ct);
        return data.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0].GetProperty("id").GetString()
            : null;
    }

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(string title, CancellationToken ct = default)
    {
        var q = title.Length > 80 ? title[..80].Trim() : title.Trim();
        if (q.Length < 3)
        {
            return [];
        }

        var data = await RequestAsync(HttpMethod.Get,
            $"/manga?filter[text]={Uri.EscapeDataString(q)}&page[limit]=6&fields[manga]=canonicalTitle,titles,slug",
            ct: ct);
        var results = new List<ScrobbleCandidate>();
        if (data.TryGetProperty("data", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var names = new List<string?> { GetString(attrs, "canonicalTitle") };
                if (attrs.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Object)
                {
                    foreach (var t in titles.EnumerateObject())
                    {
                        names.Add(t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString() : null);
                    }
                }

                var id = item.GetProperty("id").GetString() ?? "";
                var slug = GetString(attrs, "slug");
                var distinct = names.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().Distinct().ToList();
                if (distinct.Count == 0)
                {
                    continue;
                }

                results.Add(new ScrobbleCandidate(
                    id, distinct[0], distinct.Skip(1).ToList(),
                    $"https://kitsu.io/manga/{slug ?? id}"));
            }
        }

        return results;
    }

    public string EntryUrl(string remoteId) => $"https://kitsu.io/manga/{remoteId}";

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number
            ? p.GetInt32()
            : null;

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
