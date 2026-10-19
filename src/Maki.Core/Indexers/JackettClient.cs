using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Indexers;

/// <summary>
/// Client for Jackett. Search goes through its native JSON results endpoint, which reports each
/// indexer's own outcome; the indexer list and the ping use the Torznab API of the <c>all</c>
/// aggregate. Both routes authenticate by API key, unlike the rest of the API, which needs the admin cookie.
/// </summary>
public class JackettClient(IHttpClientFactory httpClientFactory, ILogger<JackettClient> logger)
    : ReleaseIndexerBase(httpClientFactory, HttpClientName)
{
    public const string HttpClientName = "jackett";

    private const string TorznabPath = "api/v2.0/indexers/all/results/torznab/api";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    public override IndexerBackend Backend => IndexerBackend.Jackett;

    private sealed record JackettResult(
        string? Title, string? Guid, string? Link, string? Details, long? Size, int? Seeders, int? Peers,
        string? InfoHash, string? MagnetUri, string? Tracker, string? TrackerId);

    private sealed record JackettIndexerOutcome(string? ID, string? Name, string? Error);

    private sealed record JackettResponse(List<JackettResult>? Results, List<JackettIndexerOutcome>? Indexers);

    /// <summary>
    /// Every queried indexer failing with nothing found is a failed search, not an empty one; an
    /// indexer failing beside others that answered is logged and the rest are used.
    /// </summary>
    public override async Task<IReadOnlyList<IndexerRelease>> SearchAsync(
        IndexerSettings s, string query, CancellationToken ct = default)
    {
        var url = new StringBuilder("api/v2.0/indexers/all/results?apikey=")
            .Append(Uri.EscapeDataString(s.ApiKey))
            .Append("&Query=").Append(Uri.EscapeDataString(query));
        foreach (var category in s.Categories ?? [])
        {
            url.Append('&').Append(Uri.EscapeDataString("Category[]")).Append('=')
                .Append(category.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var tracker in s.IndexerIds ?? [])
        {
            url.Append('&').Append(Uri.EscapeDataString("Tracker[]")).Append('=').Append(Uri.EscapeDataString(tracker));
        }

        JackettResponse? response;
        try
        {
            response = await CreateClient(s).GetFromJsonAsync<JackettResponse>(url.ToString(), Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new IndexerSearchException("Jackett answered with something other than search results", ex);
        }

        var indexers = response?.Indexers ?? [];
        var failed = indexers.Where(i => !string.IsNullOrWhiteSpace(i.Error)).ToList();
        foreach (var indexer in failed)
        {
            logger.LogWarning("Jackett indexer {Indexer} failed: {Error}", indexer.Name ?? indexer.ID, indexer.Error);
        }

        var releases = (response?.Results ?? [])
            .Select(ToRelease)
            .OfType<IndexerRelease>()
            .ToList();
        if (releases.Count == 0 && indexers.Count > 0 && failed.Count == indexers.Count)
        {
            throw new IndexerSearchException(
                $"Every Jackett indexer failed: {string.Join(", ", failed.Select(i => i.Name ?? i.ID))}");
        }

        return releases;
    }

    private static IndexerRelease? ToRelease(JackettResult r)
    {
        var guid = IndexerLinks.WithoutApiKey(r.Guid ?? r.Details ?? r.MagnetUri ?? r.Link);
        if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(guid))
        {
            return null;
        }

        // Torznab's peers counts seeders too.
        int? leechers = r is { Peers: { } peers, Seeders: { } seeders } ? Math.Max(0, peers - seeders) : null;
        var linkIsMagnet = r.MagnetUri is null && r.Link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;
        return new IndexerRelease(
            guid, r.Title, r.Size ?? 0, r.Tracker ?? r.TrackerId ?? string.Empty, r.Seeders, leechers,
            linkIsMagnet ? null : r.Link, linkIsMagnet ? r.Link : r.MagnetUri, string.IsNullOrWhiteSpace(r.InfoHash) ? null : r.InfoHash, "torrent", r.Details);
    }

    /// <summary>Jackett's configured indexers, each with the categories its caps declare.</summary>
    public override async Task<IReadOnlyList<IndexerInfo>> GetIndexersAsync(IndexerSettings s, CancellationToken ct = default)
    {
        var body = await CreateClient(s).GetStringAsync(
            $"{TorznabPath}?apikey={Uri.EscapeDataString(s.ApiKey)}&t=indexers&configured=true", ct);
        var root = Parse(body).Root ?? throw new InvalidOperationException("Jackett answered with an empty document");
        if (root.Name.LocalName == "error")
        {
            throw new InvalidOperationException(
                $"Jackett refused the indexer list: {(string?)root.Attribute("description") ?? (string?)root.Attribute("code")}");
        }

        return root.Elements("indexer")
            .Where(i => !string.Equals((string?)i.Attribute("configured"), "false", StringComparison.OrdinalIgnoreCase))
            .Select(i => (Id: (string?)i.Attribute("id"), Element: i))
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .Select(i => new IndexerInfo(
                i.Id!,
                ((string?)i.Element.Element("title"))?.Trim() is { Length: > 0 } title ? title : i.Id!,
                true,
                "torrent",
                i.Element.Element("caps")?.Element("categories")?.Elements("category")
                    .SelectMany(c => new[] { c }.Concat(c.Elements("subcat")))
                    .Select(Category)
                    .OfType<IndexerCategory>()
                    .DistinctBy(c => c.Id)
                    .OrderBy(c => c.Id)
                    .ToList() ?? []))
            .ToList();

        static IndexerCategory? Category(XElement element) =>
            int.TryParse((string?)element.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
            (string?)element.Attribute("name") is { Length: > 0 } name
                ? new IndexerCategory(id, name)
                : null;
    }

    /// <summary>
    /// A wrong key still answers 200 on some versions, with an <c>&lt;error&gt;</c> document, so the
    /// status alone does not prove the key works.
    /// </summary>
    public override async Task<bool> PingAsync(IndexerSettings s, CancellationToken ct = default)
    {
        try
        {
            using var response = await CreateClient(s).GetAsync(
                $"{TorznabPath}?apikey={Uri.EscapeDataString(s.ApiKey)}&t=caps", ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var root = Parse(await response.Content.ReadAsStringAsync(ct)).Root;
            return root is not null && root.Name.LocalName != "error";
        }
        catch
        {
            return false;
        }
    }

    private static XDocument Parse(string body)
    {
        using var reader = XmlReader.Create(new StringReader(body), XmlSettings);
        return XDocument.Load(reader);
    }

    private HttpClient CreateClient(IndexerSettings s)
    {
        var client = CreateHttpClient();
        client.BaseAddress = new Uri(s.Url.TrimEnd('/') + "/");
        return client;
    }
}

/// <summary>The backend answered, but every indexer it asked failed.</summary>
public sealed class IndexerSearchException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
