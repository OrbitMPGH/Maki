using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace Maki.Core.Indexers;

/// <summary>
/// Client for Prowlarr's aggregated search API. Maki queries Prowlarr the same
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

    public record ProwlarrCategory(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("subCategories")] List<ProwlarrCategory>? SubCategories);

    public record ProwlarrCapabilities(
        [property: JsonPropertyName("categories")] List<ProwlarrCategory>? Categories);

    public record ProwlarrIndexer(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("enable")] bool Enable,
        [property: JsonPropertyName("protocol")] string? Protocol,
        [property: JsonPropertyName("capabilities")] ProwlarrCapabilities? Capabilities);

    /// <param name="indexerIds">Limit the search to these Prowlarr indexer ids; null/empty = all indexers.</param>
    /// <param name="categories">Limit to these Torznab/Newznab category ids; null/empty = all categories.</param>
    public async Task<IReadOnlyList<ProwlarrRelease>> SearchAsync(
        string baseUrl, string apiKey, string query,
        IReadOnlyCollection<int>? indexerIds = null,
        IReadOnlyCollection<int>? categories = null,
        CancellationToken ct = default)
    {
        var url = new StringBuilder($"api/v1/search?query={Uri.EscapeDataString(query)}&type=search&limit=100");
        foreach (var id in indexerIds ?? [])
        {
            url.Append("&indexerIds=").Append(id);
        }

        foreach (var category in categories ?? [])
        {
            url.Append("&categories=").Append(category);
        }

        var client = CreateClient(baseUrl, apiKey);
        var releases = await client.GetFromJsonAsync<List<ProwlarrRelease>>(url.ToString(), ct);
        return releases ?? [];
    }

    /// <summary>All indexers configured in Prowlarr, including their category capabilities.</summary>
    public async Task<IReadOnlyList<ProwlarrIndexer>> GetIndexersAsync(
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        var client = CreateClient(baseUrl, apiKey);
        var indexers = await client.GetFromJsonAsync<List<ProwlarrIndexer>>("api/v1/indexer", ct);
        return indexers ?? [];
    }

    public async Task<bool> PingAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient(baseUrl, apiKey);
            using var response = await client.GetAsync(
                "api/v1/system/status", HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>What a release's download link served: the .torrent file, or the magnet Prowlarr redirected to.</summary>
    public sealed record TorrentLink(byte[]? File, string? Magnet);

    private const int MaxTorrentFileBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Fetches a release's download link (Prowlarr's own proxy URL, which carries its API key). A
    /// magnet-only indexer answers with a redirect to the magnet, which the handler does not follow.
    /// </summary>
    public virtual async Task<TorrentLink> FetchTorrentAsync(string downloadUrl, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Headers.Location is { } location &&
            location.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new TorrentLink(null, location.OriginalString);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxTorrentFileBytes)
        {
            throw new InvalidDataException("The download link served more than a torrent file");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxTorrentFileBytes)
            {
                throw new InvalidDataException("The download link served more than a torrent file");
            }

            buffer.Write(chunk, 0, read);
        }

        return new TorrentLink(buffer.ToArray(), null);
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
