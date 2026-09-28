using System.Net.Http.Json;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Scrobbling;

/// <summary>
/// MangaBaka tracker (REST API, Personal Access Token via x-api-key header). Also
/// used for cross-service id derivation: the public /v1/source/* endpoints map
/// AniList/MAL ids to a MangaBaka series whose `source` object carries the ids for
/// every other service.
/// </summary>
public class MangaBakaTracker(
    IHttpClientFactory httpClientFactory,
    IUserSettingsStore userSettings,
    IScrobbleTokenStore tokens,
    ScrobbleTrackerOptions options,
    ILogger<MangaBakaTracker> logger) : IScrobbleTracker
{
    public const string HttpClientName = "scrobble";

    public string Name => "mangabaka";
    public string Label => "MangaBaka";
    public bool UsesOAuth => false;

    private static readonly Dictionary<string, ScrobbleStatus> StateToInternal = new()
    {
        ["reading"] = ScrobbleStatus.Reading,
        ["rereading"] = ScrobbleStatus.Reading,
        ["completed"] = ScrobbleStatus.Completed,
        ["plan_to_read"] = ScrobbleStatus.PlanToRead,
    };

    private static readonly Dictionary<ScrobbleStatus, string> InternalToState = new()
    {
        [ScrobbleStatus.Reading] = "reading",
        [ScrobbleStatus.Completed] = "completed",
        [ScrobbleStatus.PlanToRead] = "plan_to_read",
    };

    /// <summary>
    /// Profile lookups are cached for an hour <em>per user</em>. Keyed, not single-slot: MangaBaka is
    /// a PAT per account, so one cache would hand the first caller's display name to everyone else.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime CheckedAt, string? Name)>
        _usernameCache = new();

    /// <summary>
    /// Always true: MangaBaka needs no instance-level app registration, only each user's own Personal
    /// Access Token — which is what <see cref="AuthenticatedAsync"/> checks.
    /// </summary>
    public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await ApiKeyAsync(userId, ct));

    private Task<string?> ApiKeyAsync(int userId, CancellationToken ct) =>
        userSettings.GetAsync(userId, SettingKeys.ScrobbleMangaBakaToken, ct);

    public async Task<string?> UsernameAsync(int userId, CancellationToken ct = default)
    {
        var token = await tokens.GetAsync(userId, Name, ct);
        if (!string.IsNullOrEmpty(token?.Username))
        {
            return token.Username;
        }

        if (_usernameCache.TryGetValue(userId, out var cached) &&
            DateTime.UtcNow - cached.CheckedAt < TimeSpan.FromHours(1))
        {
            return cached.Name;
        }

        string? name = null;
        try
        {
            var data = await RequestAsync(userId, HttpMethod.Get, "/v1/my/profile", auth: true, ct: ct);
            if (data.TryGetProperty("data", out var profile) && profile.ValueKind == JsonValueKind.Object)
            {
                name = GetString(profile, "preferred_username") ?? GetString(profile, "nickname") ?? GetString(profile, "id");
            }

            if (name is not null)
            {
                await tokens.SaveAsync(
                    new ScrobbleToken { UserId = userId, Service = Name, Username = name }, ct);
            }
        }
        catch (TrackerException e)
        {
            logger.LogWarning("MangaBaka profile lookup failed: {Error}", e.Message);
        }

        _usernameCache[userId] = (DateTime.UtcNow, name);
        return name;
    }

    /// <param name="userId">
    /// Whose Personal Access Token to send. Ignored when <paramref name="auth"/> is false — the
    /// public series and source endpoints need no credential, which is what makes id derivation work
    /// for a user who has never connected MangaBaka.
    /// </param>
    private async Task<JsonElement> RequestAsync(
        int userId, HttpMethod method, string path, bool auth = false, int[]? okStatuses = null,
        object? jsonBody = null, bool notFoundIsGone = false, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        string? apiKey = null;
        if (auth)
        {
            apiKey = await ApiKeyAsync(userId, ct);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new TrackerException("MangaBaka API key is not configured");
            }
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                var message = new HttpRequestMessage(method, $"{options.MangaBakaApiUrl}{path}");
                if (apiKey is not null)
                {
                    message.Headers.Add("x-api-key", apiKey);
                }

                if (jsonBody is not null)
                {
                    message.Content = JsonContent.Create(jsonBody);
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

                throw new TrackerException($"MangaBaka request failed: {e.Message}", e);
            }

            try
            {
                if ((int)response.StatusCode == 429)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                if (notFoundIsGone && (int)response.StatusCode == 404)
                {
                    throw new TrackerEntryNotFoundException($"MangaBaka {method} {path} not found (404)");
                }

                if ((int)response.StatusCode >= 400 && !(okStatuses ?? []).Contains((int)response.StatusCode))
                {
                    throw new TrackerException(
                        $"MangaBaka {method} {path} failed ({(int)response.StatusCode}): {Truncate(body)}");
                }

                return body.Length == 0
                    ? default
                    : JsonDocument.Parse(body).RootElement.Clone();
            }
            finally
            {
                response.Dispose();
            }
        }

        throw new TrackerException($"MangaBaka {method} {path} rate limited after retry");
    }

    // ---- library ----

    public async Task<RemoteEntry> GetEntryAsync(
        int userId, string remoteId, CancellationToken ct = default)
    {
        var seriesResponse = await RequestAsync(userId, HttpMethod.Get, $"/v2/series/{remoteId}",
            notFoundIsGone: true, ct: ct);
        var series = seriesResponse.TryGetProperty("data", out var sd) && sd.ValueKind == JsonValueKind.Object
            ? sd
            : default;

        var entry = default(JsonElement);
        var hasEntry = false;
        var lib = await RequestAsync(userId, HttpMethod.Get, $"/v1/my/library/{remoteId}", auth: true,
            okStatuses: [404], ct: ct);
        if (lib.ValueKind == JsonValueKind.Object && GetInt(lib, "status") == 200 &&
            lib.TryGetProperty("data", out var libData) && libData.ValueKind == JsonValueKind.Object)
        {
            entry = libData;
            hasEntry = true;
        }

        return new RemoteEntry(
            ProgressChapter: hasEntry ? ToInt(entry, "progress_chapter") ?? 0 : 0,
            ProgressVolume: hasEntry ? ToInt(entry, "progress_volume") ?? 0 : 0,
            Status: hasEntry
                ? StateToInternal.GetValueOrDefault(GetString(entry, "state") ?? "", ScrobbleStatus.Other)
                : null,
            TotalChapters: series.ValueKind == JsonValueKind.Object ? ToInt(series, "total_chapters") : null,
            TotalVolumes: series.ValueKind == JsonValueKind.Object ? ToInt(series, "final_volume") : null,
            Title: series.ValueKind == JsonValueKind.Object ? SeriesTitles(series).FirstOrDefault() ?? "" : "",
            // Library rating is on a 0–10 scale (TEXT/fractional per the dump); 0 = unrated.
            Score: hasEntry && ToInt(entry, "rating") is > 0 and <= 10 and { } r ? r : null);
    }

    public async Task UpdateAsync(
        int userId,
        string remoteId, int chapter, int volume, ScrobbleStatus status, CancellationToken ct = default)
    {
        object body = volume > 0
            ? new { state = InternalToState[status], progress_chapter = chapter, progress_volume = volume }
            : new { state = InternalToState[status], progress_chapter = chapter };

        // PATCH updates an existing entry; 404 means it isn't on the list yet -> POST
        var response = await RequestAsync(userId, HttpMethod.Patch, $"/v1/my/library/{remoteId}", auth: true,
            okStatuses: [404], jsonBody: body, ct: ct);
        if (response.ValueKind == JsonValueKind.Object && GetInt(response, "status") == 404)
        {
            await RequestAsync(userId, HttpMethod.Post, $"/v1/my/library/{remoteId}", auth: true, jsonBody: body, ct: ct);
        }
    }

    public async Task UpdateRatingAsync(
        int userId, string remoteId, int score, CancellationToken ct = default)
    {
        // MangaBaka's own rating is on a 0–10 scale (same as the dump's `rating`), so our 1–10 maps
        // directly. Same PATCH-then-POST-on-404 dance as UpdateAsync; a rejected field surfaces as a
        // TrackerException the caller treats as a best-effort miss.
        var body = new { rating = Math.Clamp(score, 0, 10) };
        var response = await RequestAsync(userId, HttpMethod.Patch, $"/v1/my/library/{remoteId}", auth: true,
            okStatuses: [404], jsonBody: body, ct: ct);
        if (response.ValueKind == JsonValueKind.Object && GetInt(response, "status") == 404)
        {
            await RequestAsync(userId, HttpMethod.Post, $"/v1/my/library/{remoteId}", auth: true, jsonBody: body, ct: ct);
        }
    }

    private static IEnumerable<string> RemoteStatesFor(ScrobbleStatus status) => status switch
    {
        ScrobbleStatus.Reading => ["reading", "rereading"],
        ScrobbleStatus.Completed => ["completed"],
        ScrobbleStatus.PlanToRead => ["plan_to_read"],
        _ => ["paused", "dropped", "considering"],
    };

    /// <summary>
    /// The v2 library listing, because v1's entries carry no series id. Each row is
    /// <c>{ entry, lists, series }</c>, and the embedded series' <c>source</c> object already holds
    /// the AniList, MAL and Kitsu ids. One pass per <c>state</c>, like MAL: nothing documents that a
    /// repeated <c>state</c> is ORed. A page that is empty or brings no new id ends the pass, so an
    /// endpoint that ignores <c>page</c> cannot spin to the page bound.
    /// </summary>
    public async Task<IReadOnlyList<RemoteListEntry>> ListAsync(
        int userId, IReadOnlyCollection<ScrobbleStatus> statuses, CancellationToken ct = default)
    {
        const int pageSize = 100;
        const int maxPages = 500;
        var entries = new List<RemoteListEntry>();
        var seen = new HashSet<long>();
        foreach (var state in statuses.SelectMany(RemoteStatesFor).Distinct())
        {
            var truncated = false;
            for (var page = 1; page <= maxPages; page++)
            {
                var data = await RequestAsync(userId, HttpMethod.Get,
                    $"/v2/my/library?limit={pageSize}&page={page}&state={state}", auth: true, ct: ct);
                if (data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                var newIds = 0;
                foreach (var row in rows.EnumerateArray())
                {
                    var entry = row.TryGetProperty("entry", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                    var series = row.TryGetProperty("series", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
                    var seriesId = series.ValueKind == JsonValueKind.Object ? ToLong(series, "id") : null;
                    seriesId ??= entry.ValueKind == JsonValueKind.Object ? ToLong(entry, "series_id") : null;
                    if (seriesId is not { } id || entry.ValueKind != JsonValueKind.Object || !seen.Add(id))
                    {
                        continue;
                    }

                    newIds++;
                    var status = StateToInternal.GetValueOrDefault(GetString(entry, "state") ?? "", ScrobbleStatus.Other);
                    if (!statuses.Contains(status))
                    {
                        continue;
                    }

                    var source = series.ValueKind == JsonValueKind.Object &&
                                 series.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object
                        ? src
                        : default;
                    entries.Add(new RemoteListEntry(
                        id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        status,
                        series.ValueKind == JsonValueKind.Object ? SeriesTitles(series).FirstOrDefault() ?? "" : "",
                        AniListId: SourceId(source, "anilist"),
                        MalId: SourceId(source, "my_anime_list"),
                        KitsuId: SourceId(source, "kitsu"),
                        MangaBakaId: id));
                }

                var hasNext = data.TryGetProperty("pagination", out var pagination) &&
                              pagination.ValueKind == JsonValueKind.Object && GetString(pagination, "next") is not null;
                truncated = newIds > 0 && hasNext && page == maxPages;
                if (newIds == 0 || !hasNext)
                {
                    break;
                }
            }

            if (truncated)
            {
                logger.LogWarning(
                    "MangaBaka {State} list for user {UserId} stopped at the {MaxPages}-page cap ({Count} entries); " +
                    "the rest of the list was not read",
                    state, userId, maxPages, entries.Count);
            }
        }

        return entries;
    }

    private static long? SourceId(JsonElement source, string site) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(site, out var entry) && entry.ValueKind == JsonValueKind.Object &&
        ToLong(entry, "id") is > 0 and { } id
            ? id
            : null;

    private static long? ToLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(
                p.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    // ---- search / matching ----

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(
        int userId, string title, CancellationToken ct = default)
    {
        var data = await RequestAsync(userId, HttpMethod.Get,
            $"/v2/series/match?q={Uri.EscapeDataString(title)}&limit=6", ct: ct);
        var results = new List<ScrobbleCandidate>();
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("data", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var series in items.EnumerateArray())
            {
                var names = SeriesTitles(series);
                if (names.Count == 0)
                {
                    continue;
                }

                var id = series.GetProperty("id").GetRawText().Trim('"');
                results.Add(new ScrobbleCandidate(id, names[0], names.Skip(1).ToList(),
                    $"https://mangabaka.org/{id}"));
            }
        }

        return results;
    }

    /// <summary>
    /// Looks up the MangaBaka series for an AniList/MAL id ("anilist" |
    /// "my-anime-list"). The returned series carries `source` ids for all other
    /// services. Null when unknown.
    /// </summary>
    public async Task<JsonElement?> ResolveFromSourceAsync(
        string source, string sourceId, CancellationToken ct = default)
    {
        JsonElement data;
        try
        {
            // Public endpoint, no credential — so no user to name. Id derivation has to work for a
            // user who has never connected MangaBaka, which is most of them.
            data = await RequestAsync(userId: 0, HttpMethod.Get,
                $"/v1/source/{source}/{sourceId}?with_series=true", okStatuses: [404], ct: ct);
        }
        catch (TrackerException)
        {
            return null;
        }

        if (data.ValueKind != JsonValueKind.Object || GetInt(data, "status") != 200 ||
            !data.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.TryGetProperty("series", out var series))
        {
            return null;
        }

        // some responses may return a list of matching series
        if (series.ValueKind == JsonValueKind.Array)
        {
            return series.GetArrayLength() > 0 ? series[0] : null;
        }

        return series.ValueKind == JsonValueKind.Object ? series : null;
    }

    public string EntryUrl(string remoteId) => $"https://mangabaka.org/{remoteId}";

    /// <summary>All known titles, English/romanized first (v2 titles: [{language, title, is_primary}]).</summary>
    private static List<string> SeriesTitles(JsonElement series)
    {
        var names = new List<string>();

        void Add(string? n)
        {
            if (!string.IsNullOrEmpty(n) && !names.Contains(n))
            {
                names.Add(n);
            }
        }

        Add(GetString(series, "title"));
        var entries = series.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array
            ? titles.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).ToList()
            : [];
        foreach (var predicate in new Func<JsonElement, bool>[]
                 {
                     t => GetString(t, "language") == "en",
                     t => GetString(t, "language") == "ja-Latn",
                     t => t.TryGetProperty("is_primary", out var p) && p.ValueKind == JsonValueKind.True,
                     _ => true,
                 })
        {
            foreach (var t in entries.Where(predicate))
            {
                Add(GetString(t, "title"));
            }
        }

        return names;
    }

    /// <summary>MangaBaka numbers can be TEXT and fractional ("112.5") — coerce like the original.</summary>
    private static int? ToInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => (int)p.GetDouble(),
            JsonValueKind.String when double.TryParse(
                p.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) => (int)value,
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
