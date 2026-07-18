using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Maki.Core.Kavita;

/// <summary>
/// Minimal Kavita client: plugin authentication (connection test + JWT for admin
/// endpoints), the scan-folder endpoint so Kavita picks up new chapters
/// immediately, and series cover/metadata push (Komf-style).
/// </summary>
public class KavitaClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "kavita";

    public record KavitaSeries(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("folderPath")] string? FolderPath,
        [property: JsonPropertyName("lowestFolderPath")] string? LowestFolderPath,
        [property: JsonPropertyName("coverImageLocked")] bool CoverImageLocked);

    private record AuthResponse([property: JsonPropertyName("token")] string? Token);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Key, string Token, DateTime FetchedAt)? _token;

    /// <summary>Validates URL + API key via Kavita's plugin authentication endpoint.</summary>
    public async Task<bool> PingAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            await GetTokenAsync(baseUrl, apiKey, force: true, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Asks Kavita to scan one folder. The path must be how *Kavita* sees the
    /// library (map it with <see cref="Paths.PathRemapper"/> when Maki and Kavita
    /// mount the library differently); Kavita matches it against its library
    /// folders and schedules a scan (it runs ~1 minute later).
    /// </summary>
    public async Task ScanFolderAsync(string baseUrl, string apiKey, string folderPath, CancellationToken ct = default)
    {
        var client = CreateClient(baseUrl);
        var response = await client.PostAsJsonAsync(
            "api/Library/scan-folder", new { apiKey, folderPath }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Finds the Kavita series for a series folder: filters by exact name (any of
    /// the given candidates — pass both the display title and the sanitized folder
    /// name, since Kavita parses its name from file names where e.g. colons are
    /// stripped), then verifies against the folder path Kavita recorded (falls
    /// back to the single name match when Kavita didn't expose a folder path).
    /// Null when Kavita hasn't scanned the series in yet.
    /// </summary>
    public async Task<KavitaSeries?> FindSeriesAsync(
        string baseUrl, string apiKey, IEnumerable<string> seriesNames, string kavitaFolderPath,
        CancellationToken ct = default)
    {
        var filter = new
        {
            statements = seriesNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new { comparison = 0 /* Equal */, field = 1 /* SeriesName */, value = name })
                .ToArray(),
            combination = 0 /* Or */,
            limitTo = 0,
            sortOptions = new { sortField = 1, isAscending = true }
        };

        using var response = await SendAuthedAsync(baseUrl, apiKey,
            client => client.PostAsJsonAsync("api/Series/all-v2", filter, ct), ct);
        response.EnsureSuccessStatusCode();
        var matches = await response.Content.ReadFromJsonAsync<List<KavitaSeries>>(cancellationToken: ct) ?? [];

        return matches.FirstOrDefault(s =>
                   PathsEqual(s.FolderPath, kavitaFolderPath) || PathsEqual(s.LowestFolderPath, kavitaFolderPath))
               ?? (matches.Count == 1 ? matches[0] : null);
    }

    public record KavitaSeriesSummary(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("localizedName")] string? LocalizedName,
        [property: JsonPropertyName("libraryId")] int LibraryId,
        [property: JsonPropertyName("pages")] int Pages,
        [property: JsonPropertyName("pagesRead")] int PagesRead);

    /// <summary>Every series Kavita knows, with the aggregate page counts (paginated FilterV2).</summary>
    public async Task<List<KavitaSeriesSummary>> GetAllSeriesAsync(
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        var filter = new
        {
            statements = Array.Empty<object>(),
            combination = 1,
            limitTo = 0,
            sortOptions = new { sortField = 1, isAscending = true }
        };

        var all = new List<KavitaSeriesSummary>();
        const int pageSize = 200;
        for (var page = 1; ; page++)
        {
            using var response = await SendAuthedAsync(baseUrl, apiKey,
                client => client.PostAsJsonAsync($"api/Series/all-v2?PageNumber={page}&PageSize={pageSize}", filter, ct), ct);
            response.EnsureSuccessStatusCode();
            var batch = await response.Content.ReadFromJsonAsync<List<KavitaSeriesSummary>>(cancellationToken: ct) ?? [];
            all.AddRange(batch);
            if (batch.Count < pageSize)
            {
                return all;
            }
        }
    }

    /// <summary>The volumes/chapters tree with per-chapter page counts and read progress.</summary>
    public async Task<List<KavitaProgress.KavitaVolumeDto>> GetVolumesAsync(
        string baseUrl, string apiKey, int kavitaSeriesId, CancellationToken ct = default)
    {
        using var response = await SendAuthedAsync(baseUrl, apiKey,
            client => client.GetAsync($"api/Series/volumes?seriesId={kavitaSeriesId}", ct), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<KavitaProgress.KavitaVolumeDto>>(cancellationToken: ct) ?? [];
    }

    /// <summary>The series' web links (comma-separated in Kavita's metadata; Maki pushes these).</summary>
    public async Task<List<string>> GetWebLinksAsync(
        string baseUrl, string apiKey, int kavitaSeriesId, CancellationToken ct = default)
    {
        var metadata = await GetSeriesMetadataAsync(baseUrl, apiKey, kavitaSeriesId, ct);
        var raw = metadata?["webLinks"]?.GetValue<string>() ?? string.Empty;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Replaces the series cover with the given image and locks it so scans keep it.</summary>
    public async Task UploadSeriesCoverAsync(
        string baseUrl, string apiKey, int kavitaSeriesId, byte[] image, CancellationToken ct = default)
    {
        using var response = await SendAuthedAsync(baseUrl, apiKey,
            client => client.PostAsJsonAsync("api/Upload/series",
                new { id = kavitaSeriesId, url = Convert.ToBase64String(image), fileName = string.Empty, lockCover = true }, ct), ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Full Kavita series metadata as JSON — patch fields and send back via <see cref="UpdateSeriesMetadataAsync"/>.</summary>
    public async Task<JsonObject?> GetSeriesMetadataAsync(
        string baseUrl, string apiKey, int kavitaSeriesId, CancellationToken ct = default)
    {
        using var response = await SendAuthedAsync(baseUrl, apiKey,
            client => client.GetAsync($"api/Series/metadata?seriesId={kavitaSeriesId}", ct), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
    }

    public async Task UpdateSeriesMetadataAsync(
        string baseUrl, string apiKey, JsonObject seriesMetadata, CancellationToken ct = default)
    {
        using var response = await SendAuthedAsync(baseUrl, apiKey,
            client => client.PostAsJsonAsync("api/Series/metadata",
                new JsonObject { ["seriesMetadata"] = seriesMetadata }, ct), ct);
        response.EnsureSuccessStatusCode();
    }

    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Sends an admin request with the cached JWT, re-authenticating once on 401.</summary>
    private async Task<HttpResponseMessage> SendAuthedAsync(
        string baseUrl, string apiKey, Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var client = CreateClient(baseUrl);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetTokenAsync(baseUrl, apiKey, force: false, ct));
        var response = await send(client);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        var retryClient = CreateClient(baseUrl);
        retryClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetTokenAsync(baseUrl, apiKey, force: true, ct));
        return await send(retryClient);
    }

    private async Task<string> GetTokenAsync(string baseUrl, string apiKey, bool force, CancellationToken ct)
    {
        var key = $"{baseUrl}|{apiKey}";
        if (!force && _token is { } cached && cached.Key == key &&
            DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromHours(6))
        {
            return cached.Token;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _token is { } raced && raced.Key == key &&
                DateTime.UtcNow - raced.FetchedAt < TimeSpan.FromHours(6))
            {
                return raced.Token;
            }

            var client = CreateClient(baseUrl);
            var response = await client.PostAsync(
                $"api/Plugin/authenticate?apiKey={Uri.EscapeDataString(apiKey)}&pluginName=Maki", null, ct);
            response.EnsureSuccessStatusCode();
            var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: ct);
            if (string.IsNullOrEmpty(auth?.Token))
            {
                throw new InvalidOperationException("Kavita returned no token");
            }

            _token = (key, auth.Token, DateTime.UtcNow);
            return auth.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private HttpClient CreateClient(string baseUrl)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        return client;
    }
}
