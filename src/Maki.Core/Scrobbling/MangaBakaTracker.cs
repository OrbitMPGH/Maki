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
    IAppSettings settings,
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

    private (DateTime CheckedAt, string? Name)? _usernameCache;

    public async Task<bool> ConfiguredAsync(CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.ScrobbleMangaBakaToken, ct));

    public Task<bool> AuthenticatedAsync(CancellationToken ct = default) => ConfiguredAsync(ct);

    public async Task<string?> UsernameAsync(CancellationToken ct = default)
    {
        var token = await tokens.GetAsync(Name, ct);
        if (!string.IsNullOrEmpty(token?.Username))
        {
            return token.Username;
        }

        if (_usernameCache is { } cached && DateTime.UtcNow - cached.CheckedAt < TimeSpan.FromHours(1))
        {
            return cached.Name;
        }

        string? name = null;
        try
        {
            var data = await RequestAsync(HttpMethod.Get, "/v1/my/profile", auth: true, ct: ct);
            if (data.TryGetProperty("data", out var profile) && profile.ValueKind == JsonValueKind.Object)
            {
                name = GetString(profile, "preferred_username") ?? GetString(profile, "nickname") ?? GetString(profile, "id");
            }

            if (name is not null)
            {
                await tokens.SaveAsync(new ScrobbleToken { Service = Name, Username = name }, ct);
            }
        }
        catch (TrackerException e)
        {
            logger.LogWarning("MangaBaka profile lookup failed: {Error}", e.Message);
        }

        _usernameCache = (DateTime.UtcNow, name);
        return name;
    }

    private async Task<JsonElement> RequestAsync(
        HttpMethod method, string path, bool auth = false, int[]? okStatuses = null,
        object? jsonBody = null, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        string? apiKey = null;
        if (auth)
        {
            apiKey = await settings.GetAsync(SettingKeys.ScrobbleMangaBakaToken, ct);
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

            if ((int)response.StatusCode == 429)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode >= 400 && !(okStatuses ?? []).Contains((int)response.StatusCode))
            {
                throw new TrackerException(
                    $"MangaBaka {method} {path} failed ({(int)response.StatusCode}): {Truncate(body)}");
            }

            return body.Length == 0
                ? default
                : JsonDocument.Parse(body).RootElement.Clone();
        }

        throw new TrackerException($"MangaBaka {method} {path} rate limited after retry");
    }

    // ---- library ----

    public async Task<RemoteEntry> GetEntryAsync(string remoteId, CancellationToken ct = default)
    {
        var seriesResponse = await RequestAsync(HttpMethod.Get, $"/v2/series/{remoteId}", ct: ct);
        var series = seriesResponse.TryGetProperty("data", out var sd) && sd.ValueKind == JsonValueKind.Object
            ? sd
            : default;

        var entry = default(JsonElement);
        var hasEntry = false;
        var lib = await RequestAsync(HttpMethod.Get, $"/v1/my/library/{remoteId}", auth: true,
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
        string remoteId, int chapter, int volume, ScrobbleStatus status, CancellationToken ct = default)
    {
        object body = volume > 0
            ? new { state = InternalToState[status], progress_chapter = chapter, progress_volume = volume }
            : new { state = InternalToState[status], progress_chapter = chapter };

        // PATCH updates an existing entry; 404 means it isn't on the list yet -> POST
        var response = await RequestAsync(HttpMethod.Patch, $"/v1/my/library/{remoteId}", auth: true,
            okStatuses: [404], jsonBody: body, ct: ct);
        if (response.ValueKind == JsonValueKind.Object && GetInt(response, "status") == 404)
        {
            await RequestAsync(HttpMethod.Post, $"/v1/my/library/{remoteId}", auth: true, jsonBody: body, ct: ct);
        }
    }

    public async Task UpdateRatingAsync(string remoteId, int score, CancellationToken ct = default)
    {
        // MangaBaka's own rating is on a 0–10 scale (same as the dump's `rating`), so our 1–10 maps
        // directly. Same PATCH-then-POST-on-404 dance as UpdateAsync; a rejected field surfaces as a
        // TrackerException the caller treats as a best-effort miss.
        var body = new { rating = Math.Clamp(score, 0, 10) };
        var response = await RequestAsync(HttpMethod.Patch, $"/v1/my/library/{remoteId}", auth: true,
            okStatuses: [404], jsonBody: body, ct: ct);
        if (response.ValueKind == JsonValueKind.Object && GetInt(response, "status") == 404)
        {
            await RequestAsync(HttpMethod.Post, $"/v1/my/library/{remoteId}", auth: true, jsonBody: body, ct: ct);
        }
    }

    // ---- search / matching ----

    public async Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(string title, CancellationToken ct = default)
    {
        var data = await RequestAsync(HttpMethod.Get,
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
            data = await RequestAsync(HttpMethod.Get,
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
