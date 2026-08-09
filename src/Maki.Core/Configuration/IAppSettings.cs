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

    /// <summary>
    /// Which Maki user Kavita's reading progress belongs to. Kavita is one external server reached
    /// with one API key, so everything it reports is one person's reading — there is no way to tell
    /// two Kavita users apart from here. Naming the owner is what keeps the whole adopt/merge/
    /// zero-delta chain in <c>ReadingProgressService</c> intact: the Kavita pass, the read-status
    /// import, the per-chapter external sync and the push-back all act as that one user.
    /// <para>
    /// Unset means "the lowest-numbered enabled admin", so an upgrade needs no configuration and a
    /// single-user install behaves exactly as before. <c>reader.pushtokavita</c> is honoured only for
    /// this user.
    /// </para>
    /// </summary>
    public const string KavitaUserId = "kavita.userid";

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

    /// <summary>
    /// Global built-in-reader display defaults, as a <see cref="Reading.ReaderPrefsSpec"/> JSON
    /// blob. A series may override the whole spec through <c>Series.ReaderPrefsJson</c>.
    /// </summary>
    public const string ReaderPrefs = "reader.prefs";

    /// <summary>
    /// "true" → after finishing a chapter in the built-in reader, also mark it read in Kavita.
    /// Default off. Only ever pushed for a series Kavita has actually reported (an adopted
    /// ReadingState row) — see ReadingProgressService for why an unmatched push double-counts.
    /// </summary>
    public const string ReaderPushToKavita = "reader.pushtokavita";

    /// <summary>
    /// Which page "/" lands on: one of <see cref="StartPage"/>'s values. Applied client-side as a
    /// <em>replacing</em> redirect, so "/" stays a valid bookmark and the nav highlight and page
    /// title work off the real path with no special cases. Unset = <see cref="StartPage.Default"/>.
    /// <para>
    /// "discover" silently falls back to Home when the local MangaBaka database isn't installed.
    /// That fallback is load-bearing, not politeness: the app already redirects /discover → / when
    /// the database is missing, so a "/" that redirected to /discover unconditionally would bounce
    /// between the two forever.
    /// </para>
    /// </summary>
    public const string UiStartPage = "ui.startpage";

    /// <summary>
    /// Which Home sections are shown and in what order, as a <see cref="HomeLayoutSpec"/> JSON
    /// blob. Also carries whether Home exists at all — people who don't read in Maki can turn the
    /// page off and get the old Library-first app back. Unset = every section, shipping order, on.
    /// <para>
    /// Interacts with <see cref="UiStartPage"/>: "home" falls back to the library when Home is
    /// disabled, the same way "discover" falls back without the local MangaBaka database.
    /// </para>
    /// </summary>
    public const string UiHomeSections = "ui.homesections";

    /// <summary>"true" → the first-time setup guide has been finished or skipped; don't show it again.</summary>
    public const string SetupCompleted = "setup.completed";

    /// <summary>
    /// Per user: the IANA time zone id their reading days are bucketed into ("Europe/Lisbon"), seeded
    /// from the browser on first load. Unset resolves to UTC.
    /// <para>
    /// Its own key rather than a field on <see cref="UserGamification"/>, because it is not a
    /// progress preference: it is the answer to "when does this person's day end", which anything
    /// day-shaped needs. Rewind currently takes a <c>utcOffsetMinutes</c> per request instead, which
    /// is fine for a year window and wrong for streaks — an offset captured at request time cannot
    /// produce a stable set of local dates for somebody who travels, or across a DST boundary.
    /// </para>
    /// </summary>
    public const string UserTimeZone = "user.timezone";

    /// <summary>
    /// Per user: whether the achievement, level and streak surfaces are shown, as a
    /// <see cref="ProgressSpec"/> JSON blob. Unset = on, streaks shown, not on the leaderboard.
    /// <para>
    /// Purely display. Progress is derived from <c>StatsEvents</c> every time it is asked for and is
    /// never materialized, so switching this off stores nothing and switching it back on recovers
    /// everything.
    /// </para>
    /// <para>
    /// The key string still reads "gamification" while the code around it says progress: the value is
    /// persisted in <c>UserSettings</c> rows, so renaming it would drop every user's stored preference
    /// back to the default. It stays until there is a data migration to move it.
    /// </para>
    /// </summary>
    public const string UserGamification = "user.gamification";

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
    /// Which embedding model to use: "base" (the only selectable tier) or "off". "large" was
    /// retired as a selectable option; any account still holding it is migrated to "base" on
    /// startup (see Program.cs).
    /// </summary>
    public const string RecommendationsEmbeddingModel = "recommendations.embeddingmodel";

    /// <summary>
    /// Per user, unlike every other <c>recommendations.*</c> key here: the Discover → Recommended
    /// panel as that person last saved it, as a <see cref="RecommendationDefaultsSpec"/> JSON blob.
    /// Unset = no default, which is the same state as a spec with nothing set — so the write path
    /// deletes the row rather than storing an empty one.
    /// </summary>
    public const string RecommendationsDefaults = "recommendations.defaults";

    /// <summary>
    /// Per user: the Discover search tab's filter panel as that person last saved it, as a
    /// <see cref="SearchDefaultsSpec"/> JSON blob. Separate from
    /// <see cref="RecommendationsDefaults"/> because the two panels are not the same panel — see
    /// that record's remarks. Unset = no default, same state as a spec with nothing set, so the
    /// write path deletes the row rather than storing an empty one.
    /// </summary>
    public const string DiscoverSearchDefaults = "discover.searchdefaults";

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

    /// <summary>
    /// CSV of source names switched off globally. A listed source is skipped by auto-matching and
    /// behaves as though every series' mapping for it were disabled — without touching the per-series
    /// <c>SourceMapping.Enabled</c> flags, so re-enabling restores exactly what the user had.
    /// Read through <c>SourceAvailability</c>, never parsed at the call site.
    /// </summary>
    public const string SourcesDisabled = "sources.disabled";

    /// <summary>"false" → the automatic sweep that re-queues Failed scraper downloads is disabled. Default on.</summary>
    public const string DownloadRetryEnabled = "download.retryenabled";

    /// <summary>How many times a Failed scraper download is auto-retried before being left alone. Default 5.</summary>
    public const string DownloadRetryMaxAttempts = "download.retrymaxattempts";
    
    /// <summary>How many unread chapters before triggering smart download. Default 5.</summary>
    public const string SmartDownloadChaptersLeft = "smartdownload.chaptersleft";
    
    /// <summary>How many chapters to download once SmartDownload triggers. Default 10.</summary>
    public const string SmartDownloadChaptersCount =  "smartdownload.chapterscount";

    /// <summary>"false" → the daily GitHub-releases update check is disabled. Default on.</summary>
    public const string UpdatesCheckForUpdates = "updates.checkforupdates";

    /// <summary>Latest version already notified about, so the update-available signal fires once per version.</summary>
    public const string UpdatesLastNotifiedVersion = "updates.lastnotifiedversion";

    /// <summary>
    /// "true" → serve the library over OPDS. Default off: the catalogue is the whole library
    /// behind a URL-embedded token, so it is opt-in rather than something an upgrade turns on.
    /// While off every OPDS route answers 404 (not 401 — a disabled server should not confirm
    /// it exists).
    /// </summary>
    public const string OpdsEnabled = "opds.enabled";

    /// <summary>
    /// "false" → don't record reading progress from OPDS page-streaming requests. Default on.
    /// The escape hatch for a client that fetches pages out of order (some fetch the last page up
    /// front to size the view), which would otherwise report progress the user never made.
    /// </summary>
    public const string OpdsTrackProgress = "opds.trackprogress";

    /// <summary>
    /// "true" → redirect HTTP to HTTPS, send HSTS, and mark the session cookie <c>Secure</c>
    /// unconditionally. Default off, because the common deployment is plain HTTP on a LAN and a
    /// <c>Secure</c> cookie there simply never comes back — the user would be unable to log in with
    /// no visible reason. Turn it on when the instance is reachable from the internet.
    /// </summary>
    public const string AuthRequireHttps = "auth.requirehttps";

    /// <summary>
    /// CSV of proxy addresses or CIDR networks whose <c>X-Forwarded-For</c>/<c>-Proto</c> headers
    /// are trusted. Empty (default) means forwarded headers are <em>ignored entirely</em>: trusting
    /// them unconditionally lets any client forge its own address, which both poisons the audit log
    /// and defeats per-IP rate limiting and lockout.
    /// </summary>
    public const string AuthTrustedProxies = "auth.trustedproxies";

    /// <summary>Failed sign-in attempts before the account locks. Default 5. "0" disables lockout.</summary>
    public const string AuthLockoutMaxAttempts = "auth.lockoutmaxattempts";

    /// <summary>How long an account stays locked, in minutes. Default 15.</summary>
    public const string AuthLockoutMinutes = "auth.lockoutminutes";

    /// <summary>Sliding session lifetime in days. Default 30.</summary>
    public const string AuthSessionDays = "auth.sessiondays";

    /// <summary>
    /// "true" → offer single sign-on. Only actually usable once
    /// <see cref="AuthOidcAuthority"/> and <see cref="AuthOidcClientId"/> are both set, which is
    /// what the enabled check tests — a half-configured provider must not put a button on the login
    /// page that can only ever fail.
    /// </summary>
    public const string AuthOidcEnabled = "auth.oidcenabled";

    /// <summary>
    /// The issuer URL, e.g. <c>https://auth.example.com/realms/maki</c>. The handler appends
    /// <c>/.well-known/openid-configuration</c> itself, so this is the issuer and not the discovery
    /// document.
    /// </summary>
    public const string AuthOidcAuthority = "auth.oidcauthority";

    public const string AuthOidcClientId = "auth.oidcclientid";

    /// <summary>
    /// Stored in plaintext like every other secret in <c>AppConfig</c>. Blank is legitimate for a
    /// public client, where PKCE is the whole proof.
    /// </summary>
    public const string AuthOidcClientSecret = "auth.oidcclientsecret";

    /// <summary>Space- or comma-separated. <c>openid</c> is always requested. Default "profile email".</summary>
    public const string AuthOidcScopes = "auth.oidcscopes";

    /// <summary>Label on the login button, e.g. "Authelia". Default "Single sign-on".</summary>
    public const string AuthOidcDisplayName = "auth.oidcdisplayname";

    /// <summary>
    /// "true" → local password login is refused for everyone except admins. Admins keep it
    /// unconditionally, and <c>MAKI_ALLOW_LOCAL_LOGIN=1</c> restores it for everyone: a broken
    /// identity provider must never be able to lock the instance's owner out of their own library.
    /// </summary>
    public const string AuthOidcOnly = "auth.oidconly";

    /// <summary>
    /// "true" → an unrecognised subject creates an account. Default <b>off</b>: with it on, anyone
    /// the identity provider will authenticate gets a Maki account, which is right for a household
    /// realm and wrong for a shared company one.
    /// </summary>
    public const string AuthOidcAutoProvision = "auth.oidcautoprovision";

    /// <summary>
    /// Claim carrying the account name, default <c>preferred_username</c>. Only used when creating
    /// an account or matching one by name; the durable link is always the subject.
    /// </summary>
    public const string AuthOidcUsernameClaim = "auth.oidcusernameclaim";

    /// <summary>
    /// <c>claim=value</c> — holding it makes the user an admin, e.g. <c>groups=maki-admins</c>. The
    /// claim name alone (no <c>=</c>) means "any value counts".
    /// </summary>
    public const string AuthOidcAdminClaim = "auth.oidcadminclaim";

    /// <summary>
    /// Claim whose values name <c>MakiPermission</c> members, e.g. a <c>groups</c> claim carrying
    /// <c>DownloadChapters</c>. Values that match nothing grant nothing.
    /// <para>
    /// Setting either this or <see cref="AuthOidcAdminClaim"/> makes the provider the authority on
    /// permissions: they are reapplied on every sign-in, so an edit made in Maki is overwritten the
    /// next time that user signs in. Leave both blank to keep permissions Maki's own.
    /// </para>
    /// </summary>
    public const string AuthOidcPermissionClaim = "auth.oidcpermissionclaim";
}
