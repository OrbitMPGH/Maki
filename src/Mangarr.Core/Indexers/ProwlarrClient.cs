using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mangarr.Core.Indexers;

/// <summary>
/// Client for Prowlarr's aggregated search API. Mangarr queries Prowlarr the same
/// way its UI does (GET /api/v1/search), so no Prowlarr-side app sync is needed.
/// </summary>
public class ProwlarrClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "prowlarr";

    public record ProwlarrRelease(
        [property: JsonPropertyName("guid")] string Guid,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("indexerId")] int IndexerId,
        [property: JsonPropertyName("indexer")] string Indexer,
        [property: JsonPropertyName("seeders")] int? Seeders,
        [property: JsonPropertyName("leechers")] int? Leechers,
        [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
        [property: JsonPropertyName("magnetUrl")] string? MagnetUrl,
        [property: JsonPropertyName("protocol")] string Protocol,
        [property: JsonPropertyName("infoUrl")] string? InfoUrl,
        [property: JsonPropertyName("ageMinutes")] double? AgeMinutes);

    public async Task<IReadOnlyList<ProwlarrRelease>> SearchAsync(
        string baseUrl, string apiKey, string query, CancellationToken ct = default)
    {
        var client = CreateClient(baseUrl, apiKey);
        var releases = await client.GetFromJsonAsync<List<ProwlarrRelease>>(
            $"api/v1/search?query={Uri.EscapeDataString(query)}&type=search&limit=100", ct);
        return releases ?? [];
    }

    public async Task<bool> PingAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient(baseUrl, apiKey);
            var response = await client.GetAsync("api/v1/system/status", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }
}
