namespace Mangarr.Core.Configuration;

/// <summary>Read access to the key/value settings store (implemented over the DB in Mangarr.Api).</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
}

public static class SettingKeys
{
    public const string FlareSolverrUrl = "flaresolverr.url";
    public const string ProwlarrUrl = "prowlarr.url";
    public const string ProwlarrApiKey = "prowlarr.apikey";
    public const string QBittorrentUrl = "qbittorrent.url";
    public const string QBittorrentUsername = "qbittorrent.username";
    public const string QBittorrentPassword = "qbittorrent.password";
    public const string QBittorrentCategory = "qbittorrent.category";
}
