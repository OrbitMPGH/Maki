using System.Collections.Concurrent;
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
    IUserSettingsStore userSettings,
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

    /// <summary>
    /// Numeric Kitsu user id per Maki user, resolved from that user's access token. Keyed rather than
    /// single-slot: with one field, the first connected account's remote id would be used to write
    /// every other account's library entries.
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _cachedUserId = new();

    /// <summary>
    /// Avoids hammering the login endpoint every status poll when credentials are bad. Per Maki user,
    /// because the credentials being tested are theirs.
    /// </summary>
    private readonly ConcurrentDictionary<int, (DateTime CheckedAt, bool Ok)> _authCache = new();

    /// <summary>
    /// Kitsu library-entry ids, keyed by Maki user and Kitsu manga id. A push reads the entry
    /// (<see cref="GetEntryAsync"/>) moments before it writes it, so the write needs no lookup of
    /// its own. Dropped whenever a write comes back 404, which is the only way an id goes stale.
    /// </summary>
    private readonly ConcurrentDictionary<(int UserId, string MangaId), string> _entryIds = new();

    /// <summary>
    /// Set when Cloudflare challenges us or Kitsu answers 429. Instance-wide rather than per user
    /// because both are decided on the source IP, so one user's block is everybody's. While it is in
    /// the future <see cref="AuthenticatedAsync"/> reports false, which drops Kitsu out of
    /// <c>ScrobbleService.ActiveTrackersAsync</c> and costs the tick no requests at all.
    /// </summary>
    private DateTime _blockedUntil = DateTime.MinValue;

    /// <summary>
    /// How long to stay off Kitsu after a challenge. A managed challenge is a JS interstitial aimed
    /// at the source IP's recent behaviour, not a transient blip, so retrying it seconds later just
    /// adds to the behaviour that caused it.
    /// </summary>
    private static readonly TimeSpan BlockCooldown = TimeSpan.FromMinutes(30);

    /// <summary>Longest 429 <c>Retry-After</c> we will sit and wait out in-band, rather than backing off the tick.</summary>
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Only the instance-level half: the OAuth app registration. Whether a given reader has supplied
    /// their own Kitsu email and password is <see cref="AuthenticatedAsync"/>'s business.
    /// </summary>
    public async Task<bool> ConfiguredAsync(CancellationToken ct = default) =>
        (await ClientIdAsync(ct)).Length > 0 && (await ClientSecretAsync(ct)).Length > 0;

    private async Task<string> ClientIdAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleKitsuClientId, ct))?.Trim() ?? "";

    private async Task<string> ClientSecretAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.ScrobbleKitsuClientSecret, ct))?.Trim() ?? "";

    // Email and password name the reader's own Kitsu account, so they come from that user's
    // settings; the client id and secret above are the instance's app registration and stay shared.
    private async Task<string> EmailAsync(int userId, CancellationToken ct) =>
        (await userSettings.GetAsync(userId, SettingKeys.ScrobbleKitsuEmail, ct))?.Trim() ?? "";

    private async Task<string> PasswordAsync(int userId, CancellationToken ct) =>
        await userSettings.GetAsync(userId, SettingKeys.ScrobbleKitsuPassword, ct) ?? "";

    /// <summary>
    /// True when a usable token exists or one can be obtained now. Doubles as the "log
    /// in" trigger since Kitsu has no redirect flow to do that from — a failed attempt
    /// is cached for a few minutes so bad credentials don't retry on every status poll.
    /// </summary>
    public async Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _blockedUntil)
        {
            return false;
        }

        if (!await ConfiguredAsync(ct) ||
            (await EmailAsync(userId, ct)).Length == 0 || (await PasswordAsync(userId, ct)).Length == 0)
        {
            return false;
        }

        var token = await tokens.GetAsync(userId, Name, ct);
        if (token is not null && token.AccessToken.Length > 0 &&
            (token.ExpiresAt is null || token.ExpiresAt > DateTime.UtcNow.AddMinutes(5)))
        {
            return true;
        }

        if (token?.RefreshToken is not null)
        {
            try
            {
                await RefreshAsync(userId, token.RefreshToken, ct);
                return true;
            }
            catch (TrackerException e)
            {
                logger.LogInformation("Kitsu token refresh failed, will re-login: {Error}", e.Message);
            }
        }

        if (_authCache.TryGetValue(userId, out var cached) &&
            DateTime.UtcNow - cached.CheckedAt < TimeSpan.FromMinutes(5))
        {
            return cached.Ok;
        }

        var ok = false;
        try
        {
            await LoginAsync(userId, ct);
            ok = true;
        }
        catch (TrackerException e)
        {
            logger.LogWarning("Kitsu login failed: {Error}", e.Message);
        }

        _authCache[userId] = (DateTime.UtcNow, ok);
        return ok;
    }

    public async Task<string?> UsernameAsync(int userId, CancellationToken ct = default) =>
        (await tokens.GetAsync(userId, Name, ct))?.Username;

    // ---- auth ----

    private async Task LoginAsync(int userId, CancellationToken ct)
    {
        var body = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = await EmailAsync(userId, ct),
            ["password"] = await PasswordAsync(userId, ct),
            ["client_id"] = await ClientIdAsync(ct),
            ["client_secret"] = await ClientSecretAsync(ct),
        }, "login", ct);
        await StoreTokenAsync(userId, body, ct);
        _cachedUserId.TryRemove(userId, out _);
        await FetchProfileAsync(userId, ct);
    }

    private async Task RefreshAsync(int userId, string refreshToken, CancellationToken ct)
    {
        var body = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = await ClientIdAsync(ct),
            ["client_secret"] = await ClientSecretAsync(ct),
        }, "token refresh", ct);
        await StoreTokenAsync(userId, body, ct);
    }

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, string what, CancellationToken ct)
    {
        ThrowIfBlocked();
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
        if (IsCloudflareChallenge(response, responseBody))
        {
            throw Block($"Kitsu {what} was Cloudflare-challenged");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException($"Kitsu {what} failed ({(int)response.StatusCode}): {Truncate(responseBody)}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private async Task StoreTokenAsync(int userId, JsonDocument body, CancellationToken ct)
    {
        var existing = await tokens.GetAsync(userId, Name, ct);
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

    private async Task<string> FetchProfileAsync(int userId, CancellationToken ct)
    {
        var data = await RequestAsync(
            userId, HttpMethod.Get, "/users?filter[self]=true&fields[users]=name", auth: true, ct: ct);
        var user = data.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0]
            : throw new TrackerException("Kitsu profile lookup returned no user");
        var id = user.GetProperty("id").GetString() ?? throw new TrackerException("Kitsu profile has no id");
        var name = user.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? GetString(attrs, "name")
            : null;

        _cachedUserId[userId] = id;
        var token = await tokens.GetAsync(userId, Name, ct);
        if (token is not null)
        {
            token.Username = name;
            await tokens.SaveAsync(token, ct);
        }

        return id;
    }

    /// <summary>The remote Kitsu id for a Maki user, resolved from their token and then cached.</summary>
    private async Task<string> RemoteUserIdAsync(int userId, CancellationToken ct) =>
        _cachedUserId.TryGetValue(userId, out var id) ? id : await FetchProfileAsync(userId, ct);

    // ---- API ----

    private async Task<JsonElement> RequestAsync(
        int userId, HttpMethod method, string path, bool auth = false, JsonObject? jsonBody = null,
        CancellationToken ct = default)
    {
        ThrowIfBlocked();
        var client = httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string? bearer = null;
            if (auth)
            {
                var token = await tokens.GetAsync(userId, Name, ct)
                            ?? throw new TrackerException("Kitsu is not connected");
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
                var token = await tokens.GetAsync(userId, Name, ct);
                if (token?.RefreshToken is not null)
                {
                    await RefreshAsync(userId, token.RefreshToken, ct);
                    continue;
                }

                await LoginAsync(userId, ct);
                continue;
            }

            if ((int)response.StatusCode == 429)
            {
                var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5);

                // A second 429, or one asking for a wait long enough that sitting on it would stall
                // the whole scrobble tick, becomes a tracker-wide cooldown instead of a sleep.
                if (attempt > 0 || wait > MaxInlineWait)
                {
                    throw Block($"Kitsu rate-limited {method} {path} (429)", wait);
                }

                await Task.Delay(wait, ct);
                continue;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (IsCloudflareChallenge(response, responseBody))
            {
                throw Block($"Kitsu {method} {path} was Cloudflare-challenged");
            }

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

    /// <summary>
    /// One request in the common case. <c>include=media</c> brings the manga's own attributes back
    /// alongside the library entry, and the entry's id is cached for the write that normally
    /// follows, so a push costs a GET and a PATCH rather than the four requests it used to.
    /// </summary>
    public async Task<RemoteEntry> GetEntryAsync(
        int userId, string remoteId, CancellationToken ct = default)
    {
        var remoteUserId = await RemoteUserIdAsync(userId, ct);
        var lib = await RequestAsync(userId, HttpMethod.Get,
            $"/library-entries?filter[userId]={remoteUserId}&filter[kind]=manga&filter[mangaId]={remoteId}" +
            "&fields[libraryEntries]=status,progress,ratingTwenty" +
            "&include=media&fields[manga]=canonicalTitle,chapterCount,volumeCount", auth: true, ct: ct);

        var entry = FirstElement(lib, "data");
        var hasEntry = entry is not null;
        var entryAttrs = entry is { } e && e.TryGetProperty("attributes", out var ea) ? ea : default;

        if (entry is { } withId && GetString(withId, "id") is { } entryId)
        {
            _entryIds[(userId, remoteId)] = entryId;
        }
        else
        {
            _entryIds.TryRemove((userId, remoteId), out _);
        }

        // "included" only carries the manga when there was a library entry to include it from, so a
        // series the user has never added still needs its totals fetched on their own. That request
        // is also what surfaces a dead Kitsu id as a 404, which clears the stale mapping upstream.
        var attrs = FirstElement(lib, "included") is { } media &&
                    media.TryGetProperty("attributes", out var ma) ? ma : default;
        if (attrs.ValueKind != JsonValueKind.Object)
        {
            var manga = await RequestAsync(userId, HttpMethod.Get,
                $"/manga/{remoteId}?fields[manga]=canonicalTitle,chapterCount,volumeCount", ct: ct);
            attrs = manga.ValueKind == JsonValueKind.Object && manga.TryGetProperty("data", out var md) &&
                    md.ValueKind == JsonValueKind.Object && md.TryGetProperty("attributes", out var a)
                ? a
                : default;
        }

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
        int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
        CancellationToken ct = default)
    {
        var attributes = new JsonObject
        {
            ["status"] = InternalToStatus[status],
            ["progress"] = chapter,
        };
        await UpsertLibraryEntryAsync(userId, remoteId, attributes, ct);
    }

    public async Task UpdateRatingAsync(
        int userId, string remoteId, int score, CancellationToken ct = default)
    {
        // 0 clears the rating; otherwise our 1-10 maps to Kitsu's 2-20 half-point scale.
        var attributes = new JsonObject
        {
            ["ratingTwenty"] = score <= 0 ? null : Math.Clamp(score, 1, 10) * 2,
        };
        await UpsertLibraryEntryAsync(userId, remoteId, attributes, ct);
    }

    private async Task UpsertLibraryEntryAsync(
        int userId, string mangaId, JsonObject attributes, CancellationToken ct)
    {
        var remoteUserId = await RemoteUserIdAsync(userId, ct);
        var key = (userId, mangaId);

        // Normally free: the push read this series' entry moments ago and cached its id. Only a
        // rating pushed without a preceding read has to look it up.
        if (!_entryIds.TryGetValue(key, out var existingId))
        {
            existingId = await FindLibraryEntryIdAsync(userId, mangaId, remoteUserId, ct);
        }

        if (existingId is not null)
        {
            var body = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["id"] = existingId,
                    ["type"] = "libraryEntries",
                    // Cloned, not attached: a JsonNode may only have one parent, and the create
                    // body below reuses these attributes when the PATCH turns out to be stale.
                    ["attributes"] = attributes.DeepClone(),
                },
            };
            try
            {
                await RequestAsync(userId, HttpMethod.Patch, $"/library-entries/{existingId}", auth: true, jsonBody: body, ct: ct);
                _entryIds[key] = existingId;
                return;
            }
            catch (TrackerEntryNotFoundException)
            {
                // The entry was removed on Kitsu since we cached its id. Fall through and create a
                // new one — rethrowing would read upstream as "this *series* is gone" and clear the
                // series' Kitsu mapping, which is a different and wrong repair.
                _entryIds.TryRemove(key, out _);
            }
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
                        // The *Kitsu* user id. Maki's own id here addressed a stranger's account,
                        // so every create was refused and retried on every tick, forever.
                        ["data"] = new JsonObject { ["id"] = remoteUserId, ["type"] = "users" },
                    },
                    ["media"] = new JsonObject
                    {
                        ["data"] = new JsonObject { ["id"] = mangaId, ["type"] = "manga" },
                    },
                },
            },
        };
        var created = await RequestAsync(userId, HttpMethod.Post, "/library-entries", auth: true, jsonBody: createBody, ct: ct);
        if (created.ValueKind == JsonValueKind.Object && created.TryGetProperty("data", out var d) &&
            GetString(d, "id") is { } createdId)
        {
            _entryIds[key] = createdId;
        }
    }

    private async Task<string?> FindLibraryEntryIdAsync(
        int userId, string mangaId, string remoteUserId, CancellationToken ct)
    {
        var data = await RequestAsync(userId, HttpMethod.Get,
            $"/library-entries?filter[userId]={remoteUserId}&filter[kind]=manga&filter[mangaId]={mangaId}" +
            "&fields[libraryEntries]=id", auth: true, ct: ct);
        return FirstElement(data, "data") is { } entry ? GetString(entry, "id") : null;
    }

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(
        int userId, string title, CancellationToken ct = default)
    {
        var q = title.Length > 80 ? title[..80].Trim() : title.Trim();
        if (q.Length < 3)
        {
            return [];
        }

        var data = await RequestAsync(userId, HttpMethod.Get,
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
                    $"https://kitsu.app/manga/{slug ?? id}"));
            }
        }

        return results;
    }

    public string EntryUrl(string remoteId) => $"https://kitsu.app/manga/{remoteId}";

    /// <summary>First element of a JSON:API top-level array member ("data", "included"), or null.</summary>
    private static JsonElement? FirstElement(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var arr) &&
        arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0]
            : null;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : null;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    private void ThrowIfBlocked()
    {
        var until = _blockedUntil;
        if (DateTime.UtcNow < until)
        {
            throw new TrackerException($"Kitsu is backed off until {until:u} after an upstream block");
        }
    }

    /// <summary>
    /// Starts (or extends) the tracker-wide cooldown and produces the exception to throw. Returned
    /// rather than thrown so the call site reads as <c>throw Block(...)</c> and the compiler can see
    /// the path terminates.
    /// </summary>
    private TrackerException Block(string reason, TimeSpan? minimum = null)
    {
        var cooldown = minimum is { } m && m > BlockCooldown ? m : BlockCooldown;
        var until = DateTime.UtcNow + cooldown;
        if (until > _blockedUntil)
        {
            _blockedUntil = until;
        }

        logger.LogWarning("{Reason}; skipping Kitsu until {Until:u}", reason, _blockedUntil);
        return new TrackerException($"{reason}; backing off until {_blockedUntil:u}");
    }

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

    private static readonly string[] ChallengeMarkers =
    [
        "Just a moment",
        "Attention Required",
        "Checking your browser",
        "Enable JavaScript and cookies to continue",
    ];

    /// <summary>
    /// Cloudflare intercepts before Kitsu's app sees the request, so a real API 403 comes
    /// back as JSON:API; this is the JS-challenge interstitial instead (title "Just a moment...").
    /// The <c>cf-mitigated</c> header is checked first because it is set on the challenge regardless
    /// of what the interstitial happens to say, which varies by ruleset and by locale.
    /// </summary>
    private static bool IsCloudflareChallenge(HttpResponseMessage response, string body)
    {
        if ((int)response.StatusCode is not (403 or 503))
        {
            return false;
        }

        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) &&
            mitigated.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) is true &&
               ChallengeMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
