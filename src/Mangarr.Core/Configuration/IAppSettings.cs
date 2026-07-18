namespace Mangarr.Core.Configuration;

/// <summary>Access to the key/value settings store (implemented over the DB in Mangarr.Api).</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

public static class SettingKeys
{
    public const string FlareSolverrUrl = "flaresolverr.url";
    public const string MangaBakaUseLocalDb = "mangabaka.uselocaldb";
    public const string MangaBakaDumpSha1 = "mangabaka.dumpsha1";
    public const string MangaBakaDumpRefreshedAt = "mangabaka.dumprefreshedat";
    public const string ProwlarrUrl = "prowlarr.url";
    public const string ProwlarrApiKey = "prowlarr.apikey";
    /// <summary>CSV of Prowlarr indexer ids to search; empty/unset = all indexers.</summary>
    public const string ProwlarrIndexerIds = "prowlarr.indexerids";
    /// <summary>CSV of Torznab category ids to search; empty/unset = all categories.</summary>
    public const string ProwlarrCategories = "prowlarr.categories";
    public const string QBittorrentUrl = "qbittorrent.url";
    public const string QBittorrentUsername = "qbittorrent.username";
    public const string QBittorrentPassword = "qbittorrent.password";
    public const string QBittorrentCategory = "qbittorrent.category";
    /// <summary>qBittorrent-side download path prefix (e.g. "/downloads" in Docker) rewritten to...</summary>
    public const string QBittorrentPathMapFrom = "qbittorrent.pathmapfrom";
    /// <summary>...the path Mangarr can actually read (e.g. @"Z:\downloads"). Empty = no rewrite.</summary>
    public const string QBittorrentPathMapTo = "qbittorrent.pathmapto";
    public const string KavitaUrl = "kavita.url";
    public const string KavitaApiKey = "kavita.apikey";
    public const string KavitaPathMapFrom = "kavita.pathmapfrom";
    public const string KavitaPathMapTo = "kavita.pathmapto";

    /// <summary>"true" → new series default to MonitorNewItems.MainOnly (specials unmonitored).</summary>
    public const string MonitoringUnmonitorSpecials = "monitoring.unmonitorspecials";

    /// <summary>
    /// How many scraper chapter downloads run at once. Read once at startup — the worker pool is
    /// fixed for the process lifetime, so a change needs a restart to take effect.
    /// </summary>
    public const string DownloadConcurrentChapters = "download.concurrentchapters";

    /// <summary>
    /// "true" → the semantic recommendation embedding index runs automatically (a few minutes
    /// after boot and daily). Default off: the CPU-heavy first pass only runs when the user
    /// clicks "Build" in settings, so dev restarts don't peg the CPU.
    /// </summary>
    public const string RecommendationsAutoIndex = "recommendations.autoindex";

    // Scrobbling (Kavita reading progress → AniList / MyAnimeList / MangaBaka)
    public const string ScrobbleAniListClientId = "scrobble.anilistclientid";
    public const string ScrobbleAniListClientSecret = "scrobble.anilistclientsecret";
    public const string ScrobbleMalClientId = "scrobble.malclientid";
    public const string ScrobbleMalClientSecret = "scrobble.malclientsecret";
    /// <summary>MangaBaka Personal Access Token ("mb-...").</summary>
    public const string ScrobbleMangaBakaToken = "scrobble.mangabakatoken";
    public const string ScrobbleIntervalMinutes = "scrobble.intervalminutes";
    /// <summary>"true" → unread Kavita series are added to the sites as plan-to-read.</summary>
    public const string ScrobblePlanToRead = "scrobble.plantoread";
    /// <summary>CSV of Kavita library ids to restrict scrobbling to; empty = all.</summary>
    public const string ScrobbleLibraryIds = "scrobble.libraryids";
    public const string ScrobbleLastSyncAt = "scrobble.lastsyncat";

    /// <summary>Per-tracker "push reading progress to this service" toggle. Unset = on.</summary>
    public static string ScrobbleReadingKey(string service) => $"scrobble.{service}.reading";

    /// <summary>Per-tracker "push ratings to this service" toggle. Unset = on.</summary>
    public static string ScrobbleRatingsKey(string service) => $"scrobble.{service}.ratings";

    /// <summary>How many backups to keep per kind (auto/manual). Oldest beyond this are pruned. Default 5.</summary>
    public const string BackupRetention = "backup.retention";
}
