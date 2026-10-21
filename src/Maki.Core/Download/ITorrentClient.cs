using Maki.Core.Configuration;

namespace Maki.Core.Download;

public enum TorrentClientKind
{
    QBittorrent,
    Transmission,
    Deluge,
}

public static class TorrentClientKinds
{
    /// <summary>The value stored in <c>downloadclient.kind</c> and sent over the API.</summary>
    public static string Key(this TorrentClientKind kind) => kind switch
    {
        TorrentClientKind.QBittorrent => "qbittorrent",
        TorrentClientKind.Transmission => "transmission",
        TorrentClientKind.Deluge => "deluge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The product name as shown to the user; not localised, it is a proper noun.</summary>
    public static string DisplayName(this TorrentClientKind kind) => kind switch
    {
        TorrentClientKind.QBittorrent => "qBittorrent",
        TorrentClientKind.Transmission => "Transmission",
        TorrentClientKind.Deluge => "Deluge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static TorrentClientKind? Parse(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        null or "" or "qbittorrent" => TorrentClientKind.QBittorrent,
        "transmission" => TorrentClientKind.Transmission,
        "deluge" => TorrentClientKind.Deluge,
        _ => null,
    };
}

/// <summary>
/// Everything a driver needs to talk to the configured client. <see cref="Label"/> is the
/// category, label or tag that marks torrents as Maki's; drivers that cannot label still accept it.
/// </summary>
public sealed record TorrentClientSettings(
    TorrentClientKind Kind,
    string Url,
    string Username,
    string Password,
    string Label)
{
    public const string DefaultLabel = "maki";
}

/// <summary>
/// A torrent as the client reports it, normalised across drivers. <see cref="ContentPath"/> is the
/// file of a single-file torrent or the root folder of a multi-file one, in the client's own view
/// of the filesystem (the caller applies the path map). <see cref="Hash"/> is lowercase hex.
/// </summary>
public sealed record ClientTorrent(
    string Hash,
    string Name,
    double Progress,
    bool IsComplete,
    string ContentPath,
    DateTimeOffset AddedAt,
    bool IsErrored,
    string? Message);

public enum TorrentClientFailure
{
    /// <summary>No answer, or an answer that was not the client's API.</summary>
    Unreachable,
    LoginFailed,
    /// <summary>The client refused the torrent or magnet.</summary>
    Rejected,
    /// <summary>The client already has the torrent outside Maki's category or label, where Maki's list cannot see it.</summary>
    HeldOutsideLabel,
}

/// <summary>A refusal Maki can name; the message is English for logs only.</summary>
public sealed class TorrentClientException(TorrentClientFailure failure, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public TorrentClientFailure Failure { get; } = failure;
}

/// <summary>
/// One torrent client protocol. Implementations are singletons that hold whatever session state
/// their client needs (cookies, CSRF tokens) and take the settings on every call, so a settings
/// change needs no restart. All hashes are returned lowercase.
/// </summary>
public interface ITorrentClient
{
    TorrentClientKind Kind { get; }

    /// <summary>False when the client authenticates with a password alone (Deluge's web UI).</summary>
    bool UsesUsername { get; }

    /// <summary>Placeholder for the URL field.</summary>
    string DefaultUrl { get; }

    /// <summary>Logs in with the given settings; throws <see cref="TorrentClientException"/> when that fails.</summary>
    Task PingAsync(TorrentClientSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Adds a magnet or a URL the client fetches itself, under <see cref="TorrentClientSettings.Label"/>.
    /// Returns the infohash when the client reports one, null when it cannot know it yet.
    /// A torrent the client already has counts as added.
    /// </summary>
    Task<string?> AddAsync(TorrentClientSettings settings, string magnetOrUrl, CancellationToken ct = default);

    /// <summary>
    /// Uploads a .torrent file under <see cref="TorrentClientSettings.Label"/>. <paramref name="infoHash"/> is
    /// the hash Maki computed from the file; a client that already has it counts as added.
    /// </summary>
    Task AddFileAsync(TorrentClientSettings settings, byte[] torrent, string infoHash, CancellationToken ct = default);

    /// <summary>
    /// Maki's torrents: those under <see cref="TorrentClientSettings.Label"/> where the client can filter
    /// or label, otherwise every torrent, since hashed rows still match and the hashless fallback has
    /// its own guards.
    /// </summary>
    Task<IReadOnlyList<ClientTorrent>> ListAsync(TorrentClientSettings settings, CancellationToken ct = default);
}

/// <summary>The one place that reads the stored download client settings and applies their defaults.</summary>
public static class DownloadClientSettingsStore
{
    /// <summary>Unset or unknown reads as qBittorrent, an unset label as <see cref="TorrentClientSettings.DefaultLabel"/>, and an unset URL as empty.</summary>
    public static async Task<TorrentClientSettings> LoadAsync(IAppSettings settings, CancellationToken ct = default) => new(
        TorrentClientKinds.Parse(await settings.GetAsync(SettingKeys.DownloadClientKind, ct)) ?? TorrentClientKind.QBittorrent,
        await settings.GetAsync(SettingKeys.DownloadClientUrl, ct) ?? string.Empty,
        await settings.GetAsync(SettingKeys.DownloadClientUsername, ct) ?? string.Empty,
        await settings.GetAsync(SettingKeys.DownloadClientPassword, ct) ?? string.Empty,
        LabelOrDefault(await settings.GetAsync(SettingKeys.DownloadClientLabel, ct)));

    public static string LabelOrDefault(string? label) =>
        string.IsNullOrWhiteSpace(label) ? TorrentClientSettings.DefaultLabel : label;
}

/// <summary>Resolves the configured kind to its driver. One instance per kind is registered in DI.</summary>
public sealed class TorrentClientRegistry(IEnumerable<ITorrentClient> clients)
{
    private readonly Dictionary<TorrentClientKind, ITorrentClient> _clients = clients.ToDictionary(c => c.Kind);

    public IReadOnlyCollection<ITorrentClient> All => _clients.Values;

    public ITorrentClient For(TorrentClientKind kind) => _clients.TryGetValue(kind, out var client)
        ? client
        : throw new InvalidOperationException($"No driver registered for {kind}");
}
