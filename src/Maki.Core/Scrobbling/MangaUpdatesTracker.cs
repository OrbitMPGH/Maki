using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Scrobbling;

/// <summary>
/// MangaUpdates tracker (REST, bearer session token). Like Kitsu there is no redirect flow: the
/// reader's username and password are exchanged for a session token lazily, the first time the
/// tracker is asked whether it is authenticated. Remote ids are the lowercase base36 slug
/// (<see cref="MangaUpdatesIds"/>), converted to the numeric <c>series_id</c> only at the HTTP boundary.
/// </summary>
public class MangaUpdatesTracker(
    IHttpClientFactory httpClientFactory,
    IUserSettingsStore userSettings,
    IScrobbleTokenStore tokens,
    ScrobbleTrackerOptions options,
    ILogger<MangaUpdatesTracker> logger) : IScrobbleTracker
{
    public const string HttpClientName = "scrobble";
    private static readonly MediaTypeWithQualityHeaderValue JsonAccept = new("application/json");

    public string Name => "mangaupdates";
    public string Label => "MangaUpdates";
    public bool UsesOAuth => false;

    // The three fixed lists every account has. 3 (unfinished) and 4 (on hold) are fixed too, and
    // custom lists get higher ids; all of those read as Other.
    private const int ReadingList = 0;
    private const int WishList = 1;
    private const int CompleteList = 2;
    private static readonly int[] FixedLists = [0, 1, 2, 3, 4];

    private static readonly Dictionary<ScrobbleStatus, int> InternalToList = new()
    {
        [ScrobbleStatus.Reading] = ReadingList,
        [ScrobbleStatus.PlanToRead] = WishList,
        [ScrobbleStatus.Completed] = CompleteList,
    };

    /// <summary>
    /// MangaUpdates answers a list write within five seconds of the previous one with 412. Writes are
    /// spaced a little wider than that per user, rather than relying on the retry.
    /// </summary>
    internal static readonly TimeSpan WriteSpacing = TimeSpan.FromSeconds(5.5);

    private static readonly TimeSpan PreconditionRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SeriesInfoTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RatingSnapshotTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AuthFailureTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BlockCooldown = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(10);
    private const int PageSize = 100;
    private const int MaxPages = 1000;

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Every wait the tracker makes goes through here, so tests can skip the sleeping.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    private readonly ConcurrentDictionary<int, (DateTime CheckedAt, bool Ok)> _authCache = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();
    private readonly ConcurrentDictionary<int, DateTime> _loggedInAt = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _writeGates = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastWrite = new();

    /// <summary>
    /// The list each (user, series) was last seen on, or null when it was on none. Read by the write
    /// that normally follows a <see cref="GetEntryAsync"/>, so it knows whether to add or update and,
    /// with <c>keepStatus</c>, which list to leave it on.
    /// </summary>
    private readonly ConcurrentDictionary<(int UserId, long SeriesId), int?> _listIds = new();

    private readonly ConcurrentDictionary<long, (DateTime FetchedAt, SeriesInfo Info)> _seriesInfo = new();

    /// <summary>
    /// The user's ratings across lists 0-4, read in one paged pass instead of one request per series.
    /// </summary>
    private readonly ConcurrentDictionary<int, (DateTime BuiltAt, Dictionary<long, double> Ratings)>
        _ratings = new();

    /// <summary>
    /// Bumped on every rating write. A snapshot whose rebuild overlapped a write is not kept, since it
    /// may have read the score from before the write.
    /// </summary>
    private readonly ConcurrentDictionary<int, int> _ratingWrites = new();

    private DateTime _blockedUntil = DateTime.MinValue;

    private sealed record SeriesInfo(string Title, bool? Completed, int? LatestChapter);

    /// <summary>A non-success answer, kept apart so callers can tell a validation 400 from the rest.</summary>
    private sealed class StatusException(int status, string message) : TrackerException(message)
    {
        public int Status { get; } = status;
    }

    private enum Relogin
    {
        Always,
        /// <summary>A 401 that may mean "no permission" rather than "expired": only re-login an old token.</summary>
        IfStale,
        Never,
    }

    public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

    private async Task<string> AccountUsernameAsync(int userId, CancellationToken ct) =>
        (await userSettings.GetAsync(userId, SettingKeys.ScrobbleMangaUpdatesUsername, ct))?.Trim() ?? "";

    private async Task<string> PasswordAsync(int userId, CancellationToken ct) =>
        await userSettings.GetAsync(userId, SettingKeys.ScrobbleMangaUpdatesPassword, ct) ?? "";

    public async Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default)
    {
        if (Now < _blockedUntil)
        {
            return false;
        }

        if ((await AccountUsernameAsync(userId, ct)).Length == 0 || (await PasswordAsync(userId, ct)).Length == 0)
        {
            return false;
        }

        if (IsUsable(await tokens.GetAsync(userId, Name, ct)))
        {
            return true;
        }

        if (_authCache.TryGetValue(userId, out var cached) && Now - cached.CheckedAt < AuthFailureTtl)
        {
            return cached.Ok;
        }

        var ok = false;
        try
        {
            await LoginAsync(userId, staleToken: null, ct);
            ok = true;
        }
        catch (TrackerException e)
        {
            logger.LogWarning("MangaUpdates login failed: {Error}", e.Message);
        }

        _authCache[userId] = (Now, ok);
        return ok;
    }

    public async Task<string?> UsernameAsync(int userId, CancellationToken ct = default) =>
        (await tokens.GetAsync(userId, Name, ct))?.Username;

    /// <summary>
    /// Drops everything cached for a user. Called when their MangaUpdates credentials change, so the
    /// new account is not judged by the old one's failed login, list placements or ratings.
    /// </summary>
    public void Forget(int userId)
    {
        _authCache.TryRemove(userId, out _);
        _loggedInAt.TryRemove(userId, out _);
        _ratings.TryRemove(userId, out _);
        foreach (var key in _listIds.Keys.Where(k => k.UserId == userId).ToList())
        {
            _listIds.TryRemove(key, out _);
        }
    }

    private bool RecentlyFailed(int userId) =>
        _authCache.TryGetValue(userId, out var cached) && !cached.Ok && Now - cached.CheckedAt < AuthFailureTtl;

    private bool IsUsable(ScrobbleToken? token) =>
        token is not null && token.AccessToken.Length > 0 &&
        (token.ExpiresAt is null || token.ExpiresAt > Now.AddMinutes(5));

    // ---- auth ----

    /// <summary>
    /// Logs in and stores the session token. Serialized per user: a rating push runs alongside the
    /// tick, and both seeing the same expired token should cost one login, not two. A usable stored
    /// token other than <paramref name="staleToken"/> means someone else logged in while this caller
    /// waited, and there is nothing left to do.
    /// </summary>
    private async Task LoginAsync(int userId, string? staleToken, CancellationToken ct)
    {
        var gate = _loginGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existing = await tokens.GetAsync(userId, Name, ct);
            if (IsUsable(existing) && (staleToken is null || existing!.AccessToken != staleToken))
            {
                return;
            }

            var body = new JsonObject
            {
                ["username"] = await AccountUsernameAsync(userId, ct),
                ["password"] = await PasswordAsync(userId, ct),
            };
            var response = await RequestAsync(userId, HttpMethod.Put, "/account/login", body,
                auth: false, ct: ct);
            var sessionToken = response.ValueKind == JsonValueKind.Object &&
                               response.TryGetProperty("context", out var context) &&
                               context.ValueKind == JsonValueKind.Object
                ? GetString(context, "session_token")
                : null;
            if (string.IsNullOrEmpty(sessionToken))
            {
                throw new TrackerException("MangaUpdates login returned no session token");
            }

            await tokens.SaveAsync(new ScrobbleToken
            {
                UserId = userId,
                Service = Name,
                AccessToken = sessionToken,
                ExpiresAt = TokenExpiry(sessionToken) ?? Now.AddDays(30),
                Username = existing?.Username,
            }, ct);
            _loggedInAt[userId] = Now;
            _authCache.TryRemove(userId, out _);
            await FetchProfileAsync(userId, sessionToken, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Best effort: a failed profile read leaves the old name and does not undo the login.</summary>
    private async Task FetchProfileAsync(int userId, string sessionToken, CancellationToken ct)
    {
        try
        {
            // The fresh token is passed straight in: going through BearerAsync from inside the login
            // gate could try to log in again and wait on the gate this call already holds.
            var profile = await RequestAsync(userId, HttpMethod.Get, "/account/profile",
                bearer: sessionToken, relogin: Relogin.Never, ct: ct);
            var token = await tokens.GetAsync(userId, Name, ct);
            if (token is not null && GetString(profile, "username") is { Length: > 0 } name)
            {
                token.Username = name;
                await tokens.SaveAsync(token, ct);
            }
        }
        catch (TrackerException e)
        {
            logger.LogInformation("MangaUpdates profile lookup failed: {Error}", e.Message);
        }
    }

    /// <summary>The <c>exp</c> claim of the session JWT, read without validating it; null when absent.</summary>
    internal static DateTime? TokenExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async Task<string> BearerAsync(int userId, CancellationToken ct)
    {
        var token = await tokens.GetAsync(userId, Name, ct);
        if (IsUsable(token))
        {
            return token!.AccessToken;
        }

        if (RecentlyFailed(userId))
        {
            throw new TrackerException("MangaUpdates login failed recently; not retrying yet");
        }

        try
        {
            await LoginAsync(userId, token?.AccessToken, ct);
        }
        catch (TrackerException)
        {
            _authCache[userId] = (Now, false);
            throw;
        }

        return (await tokens.GetAsync(userId, Name, ct))?.AccessToken
               ?? throw new TrackerException("MangaUpdates is not connected");
    }

    // ---- API ----

    private async Task<JsonElement> RequestAsync(
        int userId, HttpMethod method, string path, JsonNode? jsonBody = null, bool auth = false,
        Relogin relogin = Relogin.Always, string? bearer = null, CancellationToken ct = default)
    {
        var fixedBearer = bearer;
        ThrowIfBlocked();
        var client = httpClientFactory.CreateClient(HttpClientName);
        bool relogged = false, waitedRateLimit = false, waitedPrecondition = false, retriedTransport = false;
        while (true)
        {
            bearer = fixedBearer ?? (auth ? await BearerAsync(userId, ct) : null);

            HttpResponseMessage response;
            try
            {
                var message = new HttpRequestMessage(method, $"{options.MangaUpdatesApiUrl}{path}");
                message.Headers.Accept.Add(JsonAccept);
                if (bearer is not null)
                {
                    message.Headers.Authorization = new("Bearer", bearer);
                }

                if (jsonBody is not null)
                {
                    message.Content = new StringContent(jsonBody.ToJsonString(), Encoding.UTF8, "application/json");
                }

                response = await client.SendAsync(message, ct);
            }
            catch (HttpRequestException e)
            {
                if (!retriedTransport)
                {
                    retriedTransport = true;
                    await Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                throw new TrackerException($"MangaUpdates request failed: {e.Message}", e);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 401 && bearer is not null && !relogged && MayRelogin(userId, relogin))
                {
                    relogged = true;
                    try
                    {
                        await LoginAsync(userId, bearer, ct);
                    }
                    catch (TrackerException)
                    {
                        // Without this an unexpired but revoked token keeps AuthenticatedAsync saying
                        // yes, and every series of every tick pays a 401 and a failed login.
                        await tokens.DeleteAsync(userId, Name, ct);
                        _authCache[userId] = (Now, false);
                        throw;
                    }

                    continue;
                }

                if (status == 429)
                {
                    var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5);
                    if (waitedRateLimit || wait > MaxInlineWait)
                    {
                        throw Block($"MangaUpdates rate-limited {method} {path} (429)", wait);
                    }

                    waitedRateLimit = true;
                    await Delay(wait, ct);
                    continue;
                }

                if (status == 412 && method != HttpMethod.Get && !waitedPrecondition)
                {
                    waitedPrecondition = true;
                    await Delay(PreconditionRetryDelay, ct);
                    continue;
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                if (IsCloudflareChallenge(response, responseBody))
                {
                    throw Block($"MangaUpdates {method} {path} was Cloudflare-challenged");
                }

                if (status == 404)
                {
                    throw new TrackerEntryNotFoundException($"MangaUpdates {method} {path} not found (404)");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new StatusException(status,
                        $"MangaUpdates API {method} {path} failed ({status}): {Truncate(responseBody)}");
                }

                if (responseBody.Length == 0)
                {
                    return default;
                }

                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    return doc.RootElement.Clone();
                }
                catch (JsonException e)
                {
                    throw new TrackerException($"MangaUpdates {method} {path} returned invalid JSON", e);
                }
            }
        }
    }

    private bool MayRelogin(int userId, Relogin relogin) => !RecentlyFailed(userId) && relogin switch
    {
        Relogin.Always => true,
        Relogin.IfStale => !_loggedInAt.TryGetValue(userId, out var at) || Now - at > TimeSpan.FromMinutes(1),
        _ => false,
    };

    /// <summary>
    /// Runs one <c>/lists/series*</c> write under the user's write gate. The gate is held across the
    /// wait, the request and the timestamp, so two writers cannot both decide the spacing has passed.
    /// </summary>
    private async Task<T> ListWriteAsync<T>(int userId, Func<Task<T>> write, CancellationToken ct)
    {
        var gate = _writeGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_lastWrite.TryGetValue(userId, out var last))
            {
                var wait = last + WriteSpacing - Now;
                if (wait > TimeSpan.Zero)
                {
                    await Delay(wait, ct);
                }
            }

            try
            {
                return await write();
            }
            finally
            {
                _lastWrite[userId] = Now;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static long SeriesIdOf(string remoteId) =>
        MangaUpdatesIds.TryToSeriesId(remoteId, out var id)
            ? id
            : throw new TrackerEntryNotFoundException($"'{remoteId}' is not a MangaUpdates series id");

    public async Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default)
    {
        var seriesId = SeriesIdOf(remoteId);
        JsonElement? record = null;
        try
        {
            record = await RequestAsync(userId, HttpMethod.Get, $"/lists/series/{seriesId}", auth: true, ct: ct);
        }
        catch (TrackerEntryNotFoundException)
        {
            // Not on any of the user's lists. Whether the series itself still exists is the series
            // lookup's call below: a 404 there is what marks the mapping dead.
        }

        var info = await SeriesInfoAsync(userId, seriesId, ct);
        var listId = record is { ValueKind: JsonValueKind.Object } r ? ListIdOf(r) : null;
        _listIds[(userId, seriesId)] = listId;
        if (record is not { ValueKind: JsonValueKind.Object } entry || listId is null)
        {
            return new RemoteEntry(
                Title: info.Title,
                TotalChapters: TotalChapters(info),
                Releasing: !info.Completed);
        }

        var progress = entry.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        double? score = null;
        try
        {
            score = await RatingAsync(userId, seriesId, ct);
        }
        catch (TrackerException e)
        {
            // The score only feeds the rating import; a failed snapshot must not fail the push.
            logger.LogWarning("MangaUpdates rating snapshot failed for user {UserId}: {Error}", userId, e.Message);
        }

        return new RemoteEntry(
            ProgressChapter: GetFloor(progress, "chapter") ?? 0,
            ProgressVolume: GetFloor(progress, "volume") ?? 0,
            Status: StatusOf(listId.Value),
            TotalChapters: TotalChapters(info),
            Title: info.Title,
            Score: score is double rating and > 0 ? Math.Clamp((int)Math.Round(rating, MidpointRounding.AwayFromZero), 1, 10) : null,
            Releasing: !info.Completed);
    }

    /// <summary>
    /// MangaUpdates' latest chapter is only a total once the series is complete. Reporting it earlier
    /// would have the planner mark an ongoing series completed when the reader catches up.
    /// </summary>
    private static int? TotalChapters(SeriesInfo info) =>
        info.Completed == true && info.LatestChapter is > 0 ? info.LatestChapter : null;

    private static ScrobbleStatus StatusOf(int listId) => listId switch
    {
        ReadingList => ScrobbleStatus.Reading,
        WishList => ScrobbleStatus.PlanToRead,
        CompleteList => ScrobbleStatus.Completed,
        _ => ScrobbleStatus.Other,
    };

    private static int? ListIdOf(JsonElement record)
    {
        if (GetLong(record, "list_id") is { } id)
        {
            return (int)id;
        }

        return GetString(record, "list_type") switch
        {
            "read" => ReadingList,
            "wish" => WishList,
            "complete" => CompleteList,
            "unfinished" => 3,
            "hold" => 4,
            _ => null,
        };
    }

    private async Task<SeriesInfo> SeriesInfoAsync(int userId, long seriesId, CancellationToken ct)
    {
        if (_seriesInfo.TryGetValue(seriesId, out var cached) && Now - cached.FetchedAt < SeriesInfoTtl)
        {
            return cached.Info;
        }

        var series = await RequestAsync(userId, HttpMethod.Get, $"/series/{seriesId}", ct: ct);
        var info = new SeriesInfo(
            Decode(GetString(series, "title")),
            series.ValueKind == JsonValueKind.Object && series.TryGetProperty("completed", out var c) &&
            c.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? c.GetBoolean()
                : null,
            GetFloor(series, "latest_chapter"));
        _seriesInfo[seriesId] = (Now, info);
        return info;
    }

    private async Task<double?> RatingAsync(int userId, long seriesId, CancellationToken ct)
    {
        if (!_ratings.TryGetValue(userId, out var snapshot) || Now - snapshot.BuiltAt >= RatingSnapshotTtl)
        {
            var writesBefore = _ratingWrites.GetValueOrDefault(userId);
            var ratings = new Dictionary<long, double>();
            var seen = new HashSet<long>();
            foreach (var listId in FixedLists)
            {
                await ReadListAsync(userId, listId, result =>
                {
                    if (!result.TryGetProperty("record", out var record) ||
                        !record.TryGetProperty("series", out var series) || GetLong(series, "id") is not { } id ||
                        !seen.Add(id))
                    {
                        return false;
                    }

                    if (result.TryGetProperty("metadata", out var metadata) &&
                        GetDouble(metadata, "user_rating") is double rating and > 0)
                    {
                        ratings[id] = rating;
                    }

                    return true;
                }, ct);
            }

            if (_ratingWrites.GetValueOrDefault(userId) != writesBefore)
            {
                return null;
            }

            snapshot = (Now, ratings);
            _ratings[userId] = snapshot;
        }

        return snapshot.Ratings.TryGetValue(seriesId, out var score) ? score : null;
    }

    public async Task UpdateAsync(
        int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
        CancellationToken ct = default, bool keepStatus = false)
    {
        var seriesId = SeriesIdOf(remoteId);
        var key = (userId, seriesId);
        if (!_listIds.TryGetValue(key, out var currentList))
        {
            try
            {
                var record = await RequestAsync(userId, HttpMethod.Get, $"/lists/series/{seriesId}", auth: true, ct: ct);
                currentList = ListIdOf(record);
            }
            catch (TrackerEntryNotFoundException)
            {
                currentList = null;
            }
        }

        var listId = keepStatus && currentList is { } kept ? kept : InternalToList[status];
        var progress = new JsonObject { ["chapter"] = Math.Max(chapter, 0) };
        if (volume > 0)
        {
            progress["volume"] = volume;
        }

        var body = new JsonArray
        {
            new JsonObject
            {
                ["series"] = new JsonObject { ["id"] = seriesId },
                ["list_id"] = listId,
                ["status"] = progress,
            },
        };

        if (currentList is not null)
        {
            try
            {
                await ListWriteAsync(userId, () =>
                    RequestAsync(userId, HttpMethod.Post, "/lists/series/update", body.DeepClone(), auth: true, ct: ct), ct);
                _listIds[key] = listId;
                return;
            }
            catch (TrackerException e) when (e is TrackerEntryNotFoundException or StatusException { Status: 400 })
            {
                // Removed from the list on MangaUpdates since we looked. Adding it is the repair; a 404
                // escaping from here would read upstream as "this series is gone" and drop the mapping.
                logger.LogInformation("MangaUpdates update of {SeriesId} refused, adding instead: {Error}",
                    seriesId, e.Message);
            }
        }

        await ListWriteAsync(userId, () =>
            RequestAsync(userId, HttpMethod.Post, "/lists/series", body, auth: true, ct: ct), ct);
        _listIds[key] = listId;
    }

    /// <summary>
    /// Ratings are not list writes, so they skip the write gate (a rating push runs beside the tick and
    /// would otherwise queue behind it). A 401 here is MangaUpdates saying the account lacks the role
    /// to rate, so a fresh token is not thrown away over it.
    /// </summary>
    public async Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default)
    {
        var seriesId = SeriesIdOf(remoteId);
        if (score <= 0)
        {
            await RequestAsync(userId, HttpMethod.Delete, $"/series/{seriesId}/rating",
                auth: true, relogin: Relogin.IfStale, ct: ct);
        }
        else
        {
            await RequestAsync(userId, HttpMethod.Put, $"/series/{seriesId}/rating",
                new JsonObject { ["rating"] = Math.Clamp(score, 1, 10) },
                auth: true, relogin: Relogin.IfStale, ct: ct);
        }

        _ratingWrites.AddOrUpdate(userId, 1, (_, n) => n + 1);
        _ratings.TryRemove(userId, out _);
    }

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(
        int userId, string title, CancellationToken ct = default)
    {
        var q = title.Length > 80 ? title[..80].Trim() : title.Trim();
        if (q.Length < 3)
        {
            return [];
        }

        var data = await RequestAsync(userId, HttpMethod.Post, "/series/search",
            new JsonObject { ["search"] = q, ["page"] = 1, ["perpage"] = 6 }, ct: ct);
        var results = new List<ScrobbleCandidate>();
        if (!data.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object ||
                SlugOf(GetLong(record, "series_id"), GetString(record, "url")) is not { } slug)
            {
                continue;
            }

            var name = Decode(GetString(record, "title"));
            var hit = Decode(GetString(item, "hit_title"));
            if (name.Length == 0)
            {
                (name, hit) = (hit, "");
            }

            if (name.Length == 0)
            {
                continue;
            }

            IReadOnlyList<string> alts = hit.Length > 0 && hit != name ? [hit] : [];
            results.Add(new ScrobbleCandidate(slug, name, alts, EntryUrl(slug)));
        }

        return results;
    }

    private static IEnumerable<int> ListsFor(ScrobbleStatus status) => status switch
    {
        ScrobbleStatus.Reading => [ReadingList],
        ScrobbleStatus.PlanToRead => [WishList],
        ScrobbleStatus.Completed => [CompleteList],
        _ => [3, 4],
    };

    /// <summary>One paged pass per list. Custom lists are not read; their entries are never imported.</summary>
    public async Task<IReadOnlyList<RemoteListEntry>> ListAsync(
        int userId, IReadOnlyCollection<ScrobbleStatus> statuses, CancellationToken ct = default)
    {
        var entries = new List<RemoteListEntry>();
        var seen = new HashSet<long>();
        foreach (var listId in statuses.SelectMany(ListsFor).Distinct().Order())
        {
            var status = StatusOf(listId);
            await ReadListAsync(userId, listId, result =>
            {
                if (!result.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object ||
                    !record.TryGetProperty("series", out var series) || GetLong(series, "id") is not { } id ||
                    SlugOf(id, GetString(series, "url")) is not { } slug || !seen.Add(id))
                {
                    return false;
                }

                entries.Add(new RemoteListEntry(slug, status, Decode(GetString(series, "title")), MangaUpdatesId: slug));
                return true;
            }, ct);
        }

        return entries;
    }

    /// <summary>
    /// Pages <c>POST /lists/{id}/search</c> to the end: an empty page, as many results read as
    /// <c>total_hits</c> says there are (counted, not inferred from <c>perpage</c>, which the server may
    /// cap), a short page when there is no <c>total_hits</c>, or a page where <paramref name="onResult"/>
    /// took nothing new (an API ignoring <c>page</c> would otherwise loop to the cap). A list the
    /// account does not have reads as empty.
    /// </summary>
    private async Task ReadListAsync(int userId, int listId, Func<JsonElement, bool> onResult, CancellationToken ct)
    {
        long read = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            JsonElement data;
            try
            {
                data = await RequestAsync(userId, HttpMethod.Post, $"/lists/{listId}/search",
                    new JsonObject { ["page"] = page, ["perpage"] = PageSize }, auth: true, ct: ct);
            }
            catch (TrackerEntryNotFoundException)
            {
                return;
            }

            if (!data.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return;
            }

            var took = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind == JsonValueKind.Object && onResult(result))
                {
                    took++;
                }
            }

            read += results.GetArrayLength();
            var total = GetLong(data, "total_hits");
            if (took == 0 || (total is { } t ? read >= t : results.GetArrayLength() < PageSize))
            {
                return;
            }

            if (page == MaxPages)
            {
                logger.LogWarning(
                    "MangaUpdates list {ListId} for user {UserId} stopped at the {Max}-page cap; the rest was not read",
                    listId, userId, MaxPages);
            }
        }
    }

    public string EntryUrl(string remoteId) => $"https://www.mangaupdates.com/series/{remoteId}";

    private static string? SlugOf(long? seriesId, string? url)
    {
        if (seriesId is long id and > 0)
        {
            return MangaUpdatesIds.ToSlug(id);
        }

        return url is not null && MangaUpdatesIds.TryParse(url, out var slug) ? slug : null;
    }

    private static string Decode(string? text) => WebUtility.HtmlDecode(text ?? "").Trim();

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
        if (Now < until)
        {
            throw new TrackerException($"MangaUpdates is backed off until {until:u} after an upstream block");
        }
    }

    private TrackerException Block(string reason, TimeSpan? minimum = null)
    {
        var cooldown = minimum is { } m && m > BlockCooldown ? m : BlockCooldown;
        var until = Now + cooldown;
        if (until > _blockedUntil)
        {
            _blockedUntil = until;
        }

        logger.LogWarning("{Reason}; skipping MangaUpdates until {Until:u}", reason, _blockedUntil);
        return new TrackerException($"{reason}; backing off until {_blockedUntil:u}");
    }

    private static long? GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var value)
            ? value
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : null;

    private static int? GetFloor(JsonElement element, string name) =>
        GetDouble(element, name) is { } value && value >= 0
            ? (int)Math.Min(Math.Floor(value), int.MaxValue)
            : null;

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
