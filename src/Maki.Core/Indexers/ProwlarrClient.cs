using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Core.Indexers;

/// <summary>
/// Client for Prowlarr's aggregated search API. Maki queries Prowlarr the same
/// way its UI does (GET /api/v1/search), so no Prowlarr-side app sync is needed.
/// </summary>
public class ProwlarrClient(IHttpClientFactory httpClientFactory) : ReleaseIndexerBase(httpClientFactory, HttpClientName)
{
    public const string HttpClientName = "prowlarr";

    public override IndexerBackend Backend => IndexerBackend.Prowlarr;

    private sealed record ProwlarrRelease(
        [property: JsonPropertyName("guid")] string Guid,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("indexer")] string? Indexer,
        [property: JsonPropertyName("seeders")] int? Seeders,
        [property: JsonPropertyName("leechers")] int? Leechers,
        [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
        [property: JsonPropertyName("magnetUrl")] string? MagnetUrl,
        [property: JsonPropertyName("infoHash")] string? InfoHash,
        [property: JsonPropertyName("protocol")] string? Protocol,
        [property: JsonPropertyName("infoUrl")] string? InfoUrl);

    private sealed record ProwlarrCategory(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("subCategories")] List<ProwlarrCategory>? SubCategories);

    private sealed record ProwlarrCapabilities(
        [property: JsonPropertyName("categories")] List<ProwlarrCategory>? Categories);

    private sealed record ProwlarrIndexer(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("enable")] bool Enable,
        [property: JsonPropertyName("protocol")] string? Protocol,
        [property: JsonPropertyName("capabilities")] ProwlarrCapabilities? Capabilities);

    public override async Task<IReadOnlyList<IndexerRelease>> SearchAsync(
        IndexerSettings s, string query, CancellationToken ct = default)
    {
        var url = new StringBuilder($"api/v1/search?query={Uri.EscapeDataString(query)}&type=search&limit=100");
        foreach (var id in NumericIds(s.IndexerIds))
        {
            url.Append("&indexerIds=").Append(id.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var category in s.Categories ?? [])
        {
            url.Append("&categories=").Append(category.ToString(CultureInfo.InvariantCulture));
        }

        var client = CreateClient(s);
        List<ProwlarrRelease> releases;
        try
        {
            releases = await client.GetFromJsonAsync<List<ProwlarrRelease>>(url.ToString(), ct) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new IndexerSearchException("Prowlarr answered with something other than search results", ex);
        }

        return releases.Select(ToRelease).ToList();
    }

    /// <summary>
    /// Prowlarr replaces every magnet with its own proxy link, which carries its API key in the query.
    /// Only a real <c>magnet:</c> stays a magnet; a proxy link is a download URL, fetched and hashed like one.
    /// </summary>
    private static IndexerRelease ToRelease(ProwlarrRelease r)
    {
        var magnet = r.MagnetUrl?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true ? r.MagnetUrl : null;
        var download = r.DownloadUrl ?? (magnet is null && !string.IsNullOrWhiteSpace(r.MagnetUrl) ? r.MagnetUrl : null);
        return new IndexerRelease(
            r.Guid, r.Title, r.Size, r.Indexer ?? string.Empty, r.Seeders, r.Leechers,
            download, magnet, string.IsNullOrWhiteSpace(r.InfoHash) ? null : r.InfoHash,
            r.Protocol ?? string.Empty, r.InfoUrl);
    }

    /// <summary>All indexers configured in Prowlarr, including their category capabilities.</summary>
    public override async Task<IReadOnlyList<IndexerInfo>> GetIndexersAsync(IndexerSettings s, CancellationToken ct = default)
    {
        var client = CreateClient(s);
        var indexers = await client.GetFromJsonAsync<List<ProwlarrIndexer>>("api/v1/indexer", ct) ?? [];
        return indexers
            .Select(i => new IndexerInfo(
                i.Id.ToString(CultureInfo.InvariantCulture),
                i.Name ?? string.Empty,
                i.Enable,
                i.Protocol,
                Flatten(i.Capabilities?.Categories)
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => new IndexerCategory(c.Id, c.Name!))
                    .DistinctBy(c => c.Id)
                    .OrderBy(c => c.Id)
                    .ToList()))
            .ToList();

        static IEnumerable<ProwlarrCategory> Flatten(IEnumerable<ProwlarrCategory>? categories) =>
            categories?.SelectMany(c => new[] { c }.Concat(Flatten(c.SubCategories))) ?? [];
    }

    public override async Task<bool> PingAsync(IndexerSettings s, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient(s);
            using var response = await client.GetAsync(
                "api/v1/system/status", HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stored ids are text so Jackett's names fit the same column; Prowlarr's are ints.</summary>
    private static IEnumerable<int> NumericIds(IReadOnlyList<string>? ids) =>
        (ids ?? [])
        .Select(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null)
        .OfType<int>();

    private HttpClient CreateClient(IndexerSettings s)
    {
        var client = CreateHttpClient();
        client.BaseAddress = new Uri(s.Url.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", s.ApiKey);
        return client;
    }
}
