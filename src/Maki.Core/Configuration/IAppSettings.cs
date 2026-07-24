using Maki.Core.Naming;

namespace Maki.Core.Configuration;

/// <summary>Access to the key/value settings store (implemented over the DB in Maki.Api).</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

public static class SettingKeys
{
    public const string FlareSolverrUrl = "flaresolverr.url";

    /// <summary>
    /// Optional Chromium <c>--host-resolver-rules</c> for the MangaFire headless browser, e.g.
    /// "MAP mangafire.to 188.114.96.1". Only needed where the Maki host can't resolve the site's
    /// DNS itself (some dev machines); unset in normal deployments, which resolve normally.
    /// </summary>
    public const string MangaFireBrowserHostResolverRules = "mangafire.browserhostresolverrules";
    public const string MangaBakaUseLocalDb = "mangabaka.uselocaldb";
    public const string MangaBakaDumpSha1 = "mangabaka.dumpsha1";
    public const string MangaBakaDumpRefreshedAt = "mangabaka.dumprefreshedat";

    /// <summary>
    /// "true" → download the larger "full" MangaBaka dump (~4.6 GB vs ~3.5 GB) that carries each
    /// source's raw response, including the MangaUpdates description the embedding indexer prefers.
    /// Default off: only a machine that *builds* the embedding index locally benefits; users who
    /// download the prebuilt index never need it.
    /// </summary>
    public const string MangaBakaUseFullDump = "mangabaka.usefulldump";
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
    /// <summary>...the path Maki can actually read (e.g. @"Z:\downloads"). Empty = no rewrite.</summary>
    public const string QBittorrentPathMapTo = "qbittorrent.pathmapto";
    public const string KavitaUrl = "kavita.url";
    public const string KavitaApiKey = "kavita.apikey";
    public const string KavitaPathMapFrom = "kavita.pathmapfrom";
    public const string KavitaPathMapTo = "kavita.pathmapto";

    /// <summary>"true" → new series default to MonitorNewItems.MainOnly (specials unmonitored).</summary>
    public const string MonitoringUnmonitorSpecials = "monitoring.unmonitorspecials";

    /// <summary>
    /// "false" → don't rewrite ComicInfo.xml inside files Maki adopts from disk (torrent grabs,
    /// manual imports). Chapters Maki downloads itself from a source always get a fresh ComicInfo —
    /// that CBZ is built by Maki, not an existing file being modified. Default on.
    /// </summary>
    public const string LibraryWriteComicInfo = "library.writecomicinfo";

    /// <summary>
    /// One of <see cref="Naming.FolderNamingMode"/>'s values. Controls whether an imported
    /// series' on-disk folder is renamed to Maki's sanitized-title standard, and which folder
    /// name future chapter downloads for that series use. Unset = <see cref="Naming.FolderNamingMode.Default"/>.
    /// </summary>
    public const string LibraryFolderNamingMode = "library.foldernamingmode";

    /// <summary>"true" → the first-time setup guide has been finished or skipped; don't show it again.</summary>
    public const string SetupCompleted = "setup.completed";

    /// <summary>
    /// Highest MangaBaka <c>content_rating</c> ("safe"/"suggestive"/"erotica"/"pornographic")
    /// shown in metadata search results ("Add Series" search box); everything at or below it in
    /// that order is included. Unset defaults to "erotica" (excludes only pornographic).
    /// Recommendations/Discover always exclude pornographic entries regardless of this setting —
    /// they're never embedded into the index in the first place, so there's nothing to toggle there.
    /// </summary>
    public const string DiscoverMaxContentRating = "discover.maxcontentrating";

    /// <summary>
    /// How many scraper chapter downloads run at once. Read once at startup — the worker pool is
    /// fixed for the process lifetime, so a change needs a restart to take effect.
    /// </summary>
    public const string DownloadConcurrentChapters = "download.concurrentchapters";

    /// <summary>
    /// "false" → never download the prebuilt embedding index, always build it locally. Default on:
    /// the vectors are derived entirely from the public MangaBaka dump, so downloading them saves
    /// every install ~an hour of CPU for a byte-identical result.
    /// </summary>
    public const string RecommendationsPrebuiltEnabled = "recommendations.prebuiltenabled";

    /// <summary>
    /// Manifest URL for the prebuilt index. Overridable for forks and air-gapped mirrors — it
    /// points at a SQLite database this instance will install, so only trusted sources belong here.
    /// </summary>
    public const string RecommendationsPrebuiltUrl = "recommendations.prebuilturl";

    /// <summary>`generatedAt` of the installed prebuilt index; how freshness is judged.</summary>
    public const string RecommendationsPrebuiltGeneratedAt = "recommendations.prebuiltgeneratedat";

    /// <summary>
    /// Which embedding model to use: "base" (default, ~240 MB RAM) or "large" (higher quality,
    /// ~500 MB RAM and a bigger download). The models have different dimensionalities, so changing
    /// this re-embeds the whole index; it takes effect on restart.
    /// </summary>
    public const string RecommendationsEmbeddingModel = "recommendations.embeddingmodel";

    // Scrobbling (Kavita reading progress → AniList / MyAnimeList / MangaBaka)
    public const string ScrobbleAniListClientId = "scrobble.anilistclientid";
    public const string ScrobbleAniListClientSecret = "scrobble.anilistclientsecret";
    public const string ScrobbleMalClientId = "scrobble.malclientid";
    public const string ScrobbleMalClientSecret = "scrobble.malclientsecret";
    /// <summary>MangaBaka Personal Access Token ("mb-...").</summary>
    public const string ScrobbleMangaBakaToken = "scrobble.mangabakatoken";
    /// <summary>Kitsu OAuth app credentials for the password grant.</summary>
    public const string ScrobbleKitsuClientId = "scrobble.kitsuclientid";
    public const string ScrobbleKitsuClientSecret = "scrobble.kitsuclientsecret";
    /// <summary>Kitsu account email/password — exchanged for a token via the password grant (no redirect flow).</summary>
    public const string ScrobbleKitsuEmail = "scrobble.kitsuemail";
    public const string ScrobbleKitsuPassword = "scrobble.kitsupassword";
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

    /// <summary>
    /// CSV of source names in preferred order (e.g. "mangadex,mangafire,mangapill"), applied when
    /// auto-matching sets each mapping's Priority. Sources not listed rank after listed ones, in
    /// SourceRegistry.All order. Empty/unset = SourceRegistry.All order (registration order).
    /// </summary>
    public const string SourcePriorityOrder = "sources.priorityorder";

    /// <summary>"false" → the automatic sweep that re-queues Failed scraper downloads is disabled. Default on.</summary>
    public const string DownloadRetryEnabled = "download.retryenabled";

    /// <summary>How many times a Failed scraper download is auto-retried before being left alone. Default 5.</summary>
    public const string DownloadRetryMaxAttempts = "download.retrymaxattempts";

    /// <summary>"false" → the daily GitHub-releases update check is disabled. Default on.</summary>
    public const string UpdatesCheckForUpdates = "updates.checkforupdates";

    /// <summary>Latest version already notified about, so the update-available signal fires once per version.</summary>
    public const string UpdatesLastNotifiedVersion = "updates.lastnotifiedversion";
}
