using System.Globalization;
using Maki.Core.Configuration;

namespace Maki.Core.Indexers;

public enum IndexerBackend
{
    Prowlarr,
    Jackett,
}

public static class IndexerBackends
{
    /// <summary>The value stored in <c>indexer.backend</c> and sent over the API.</summary>
    public static string Key(this IndexerBackend backend) => backend switch
    {
        IndexerBackend.Prowlarr => "prowlarr",
        IndexerBackend.Jackett => "jackett",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    /// <summary>The product name as shown to the user; not localised, it is a proper noun.</summary>
    public static string DisplayName(this IndexerBackend backend) => backend switch
    {
        IndexerBackend.Prowlarr => "Prowlarr",
        IndexerBackend.Jackett => "Jackett",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    public static IndexerBackend? Parse(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        null or "" or "prowlarr" => IndexerBackend.Prowlarr,
        "jackett" => IndexerBackend.Jackett,
        _ => null,
    };
}

/// <param name="IndexerIds">Indexers to search, as the backend names them; null searches every one.</param>
/// <param name="Categories">Torznab category ids to search; null searches every category.</param>
public sealed record IndexerSettings(
    IndexerBackend Backend,
    string Url,
    string ApiKey,
    IReadOnlyList<string>? IndexerIds,
    IReadOnlyList<int>? Categories)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record IndexerRelease(
    string Guid,
    string Title,
    long Size,
    string Indexer,
    int? Seeders,
    int? Leechers,
    string? DownloadUrl,
    string? MagnetUrl,
    string? InfoHash,
    string Protocol,
    string? InfoUrl);

public sealed record IndexerCategory(int Id, string Name);

/// <param name="Categories">Flattened, distinct by id, ordered by id, nameless ones dropped.</param>
public sealed record IndexerInfo(
    string Id,
    string Name,
    bool Enabled,
    string? Protocol,
    IReadOnlyList<IndexerCategory> Categories);

/// <summary>What a release's download link served: the .torrent file, or the magnet the indexer redirected to.</summary>
public sealed record TorrentLink(byte[]? File, string? Magnet);

/// <summary>
/// One indexer aggregator. Implementations are singletons and take the settings on every call, so a
/// settings change needs no restart.
/// </summary>
public interface IReleaseIndexer
{
    IndexerBackend Backend { get; }

    Task<IReadOnlyList<IndexerRelease>> SearchAsync(IndexerSettings s, string query, CancellationToken ct = default);

    Task<IReadOnlyList<IndexerInfo>> GetIndexersAsync(IndexerSettings s, CancellationToken ct = default);

    Task<bool> PingAsync(IndexerSettings s, CancellationToken ct = default);

    /// <summary>Fetches a release's download link, which is the backend's own proxy URL carrying its API key.</summary>
    Task<TorrentLink> FetchTorrentAsync(string downloadUrl, CancellationToken ct = default);
}

/// <summary>Resolves a backend to its client. One instance per backend is registered in DI.</summary>
public sealed class IndexerRegistry(IEnumerable<IReleaseIndexer> indexers)
{
    private readonly Dictionary<IndexerBackend, IReleaseIndexer> _indexers = indexers.ToDictionary(i => i.Backend);

    public IReadOnlyCollection<IReleaseIndexer> All => _indexers.Values;

    public IReleaseIndexer For(IndexerBackend backend) => _indexers.TryGetValue(backend, out var indexer)
        ? indexer
        : throw new InvalidOperationException($"No indexer registered for {backend}");
}

/// <summary>Where each backend's settings live, and the one place that reads them.</summary>
public static class IndexerSettingsStore
{
    public sealed record Keys(string Url, string ApiKey, string IndexerIds, string Categories);

    public static Keys KeysFor(IndexerBackend backend) => backend switch
    {
        IndexerBackend.Prowlarr => new(SettingKeys.ProwlarrUrl, SettingKeys.ProwlarrApiKey,
            SettingKeys.ProwlarrIndexerIds, SettingKeys.ProwlarrCategories),
        IndexerBackend.Jackett => new(SettingKeys.JackettUrl, SettingKeys.JackettApiKey,
            SettingKeys.JackettIndexerIds, SettingKeys.JackettCategories),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    /// <summary>The stored backend; unset or unknown reads as Prowlarr.</summary>
    public static async Task<IndexerBackend> ActiveBackendAsync(IAppSettings settings, CancellationToken ct = default) =>
        IndexerBackends.Parse(await settings.GetAsync(SettingKeys.IndexerBackend, ct)) ?? IndexerBackend.Prowlarr;

    /// <summary>The settings of <paramref name="backend"/>, or of the active backend when null. Unset values read as empty.</summary>
    public static async Task<IndexerSettings> LoadAsync(
        IAppSettings settings, CancellationToken ct = default, IndexerBackend? backend = null)
    {
        var chosen = backend ?? await ActiveBackendAsync(settings, ct);
        var keys = KeysFor(chosen);
        return new IndexerSettings(
            chosen,
            await settings.GetAsync(keys.Url, ct) ?? string.Empty,
            await settings.GetAsync(keys.ApiKey, ct) ?? string.Empty,
            ParseIds(await settings.GetAsync(keys.IndexerIds, ct)),
            ParseCategories(await settings.GetAsync(keys.Categories, ct)));
    }

    public static IReadOnlyList<string>? ParseIds(string? csv)
    {
        var ids = Split(csv).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count > 0 ? ids : null;
    }

    public static IReadOnlyList<int>? ParseCategories(string? csv)
    {
        var ids = Split(csv)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
            .OfType<int>()
            .Distinct()
            .ToList();
        return ids.Count > 0 ? ids : null;
    }

    private static IEnumerable<string> Split(string? csv) => string.IsNullOrWhiteSpace(csv)
        ? []
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
