using Maki.Api;
using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Inbox;
using Maki.Core.Metadata;
using Maki.Core.Notifications;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Core.Configuration;
using Maki.Sources.Asura;
using Maki.Sources.Atsumaru;
using Maki.Sources.FlameComics;
using Maki.Sources.MangaDex;
using Maki.Sources.MangaFire;
using Maki.Sources.MangaKatana;
using Maki.Sources.MangaPill;
using Maki.Sources.MangaPlus;
using Maki.Sources.TCBScans;
using Maki.Sources.WeebCentral;
using Maki.Sources.Webtoons;
using System.Net;
using Maki.Sources.TopManhua;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Serilog;

var paths = new AppPaths();

// Apply a restore staged by a previous run before anything reads config.json or opens the DB.
RestoreBootstrap.ApplyPendingRestore(paths);

var configFile = new ConfigFileProvider(paths);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<Serilog.Events.LogEventLevel>(configFile.Config.LogLevel, true, out var level)
        ? level
        : Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(paths.LogDir, "maki-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();
    builder.WebHost.UseUrls($"http://*:{configFile.Config.Port}");

    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(configFile);

    builder.Services.AddDbContext<MakiDbContext>(options =>
        options.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared"));

    builder.Services.AddScoped<BackupService>();

    // MangaBaka: uncached requests are limited to 30/min (search) and 120/min (lookup).
    // Replenish smoothly (1 token / 2 s = 30/min) instead of in per-minute chunks, and
    // keep the client timeout well above the worst queue wait — library scans fire one
    // search per folder and the queue delay counts toward the HttpClient timeout.
    var mangaBakaLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(2), burst: 10);
    builder.Services.AddHttpClient(MangaBakaProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.mangabaka.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/Maki)");
            client.Timeout = TimeSpan.FromMinutes(3);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaBakaLimiter))
        .AddHttpMessageHandler(() => new TransientRetryHandler());

    builder.Services.AddHttpClient("covers", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
            client.Timeout = TimeSpan.FromSeconds(60);
        })
        // Redirects are not followed automatically. SearchController's cover proxy fetches a
        // caller-supplied URL and validates its host against the source's allowlist; an automatic
        // redirect would sidestep that check entirely, so the proxy follows hops itself and re-checks
        // each one. CoverService only ever fetches URLs a source produced, so losing auto-redirect
        // there is a non-event.
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler(() => new TransientRetryHandler());

    // Bulk dump downloads (~350 MB nightly snapshot) bypass the rate limiter — a single
    // long-running request, and the timeout must cover the full transfer on slow links.
    builder.Services.AddHttpClient(MangaBakaDumpService.HttpClientName, client =>
    {
        client.BaseAddress = new Uri("https://api.mangabaka.org/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/Maki)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });

    // MAL reviews for the Discover detail card, scraped from MAL's public reviews page. (Jikan,
    // the unofficial MAL API, has a chronically-broken /reviews endpoint — see MalReviewClient.)
    // Fetches are user-triggered and cached, so a gentle rate limit and a browser UA suffice.
    var malLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(MalReviewClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://myanimelist.net/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.Timeout = TimeSpan.FromSeconds(20);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(malLimiter))
        .AddHttpMessageHandler(() => new TransientRetryHandler());
    builder.Services.AddSingleton<MalReviewClient>();

    builder.Services.AddSingleton(new MangaBakaDumpOptions(paths.MangaBakaDbPath, paths.CacheDir));
    builder.Services.AddSingleton<MangaBakaDumpService>();
    builder.Services.AddSingleton<MangaBakaLocalStore>();
    // Credits and the title-index term dictionary, both RAM-resident and both built lazily from the
    // dump. They are what answer "junji ito" and what let a misspelled title still find its series;
    // DiscoverCacheWarmJob builds them so the cost never lands on a keystroke.
    builder.Services.AddSingleton<CatalogueIndexCache>();
    builder.Services.AddSingleton(SearchTuning.Default.Catalogue);
    builder.Services.AddSingleton<IMetadataProvider, MangaBakaProvider>();
    builder.Services.AddSingleton<CoverService>();
    builder.Services.AddSingleton<RecommendationService>();
    builder.Services.AddSingleton<SimilarSeriesService>();
    builder.Services.AddSingleton<DiscoverService>();

    // Semantic recommendations: a local ONNX embedding model (~110 MB, downloaded on first
    // use) turns each series' description into a vector so Discover can match on "feel", not
    // just shared genre labels. The one-time index pass runs as a background job.
    builder.Services.AddHttpClient(EmbeddingModelStore.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    // The model is a user setting (base default, or "off"). Resolved lazily so the setting is read
    // after the DB is migrated; EmbeddingModelSwitcher then mutates it live.
    builder.Services.AddSingleton(sp =>
    {
        var settings = sp.GetRequiredService<Maki.Core.Configuration.IAppSettings>();
        var kind = settings.GetAsync(SettingKeys.RecommendationsEmbeddingModel).GetAwaiter().GetResult();
        // "large" was retired as a selectable model; migrate any account still on it to base.
        if (string.Equals(kind, "large", StringComparison.OrdinalIgnoreCase))
        {
            kind = "base";
            settings.SetAsync(SettingKeys.RecommendationsEmbeddingModel, kind).GetAwaiter().GetResult();
        }
        return new EmbeddingOptions(
            paths.ModelsDir, paths.EmbeddingsDbPath, paths.CacheDir, EmbeddingModelProfile.Resolve(kind))
        {
            Enabled = !EmbeddingModelProfile.IsOff(kind),
        };
    });
    builder.Services.AddSingleton<EmbeddingModelStore>();
    builder.Services.AddSingleton<TextEmbedder>();
    builder.Services.AddSingleton<EmbeddingStore>();
    builder.Services.AddSingleton<EmbeddingIndexStatus>();
    builder.Services.AddSingleton<SeriesEmbeddingIndexer>();
    builder.Services.AddSingleton<SemanticRecommender>();

    // Natural-language Discover search reads the same vectors, but per keystroke rather than per
    // background job, so it holds the index in memory (int8-quantized) instead of re-reading the
    // BLOBs. Built lazily on the first search; dropped after each indexing pass.
    builder.Services.AddSingleton<VectorIndexCache>();
    // Channel weights and floors live in one record so distribution/eval-search.cs can sweep them
    // against the labelled query set; nothing changes them at runtime.
    builder.Services.AddSingleton(SearchTuning.Default);
    builder.Services.AddSingleton<SemanticSearcher>();

    // The published index is ~70 MB compressed; give it room to arrive on a slow line.
    builder.Services.AddHttpClient(PrebuiltIndexInstaller.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton<PrebuiltIndexInstaller>();

    // Live model switching: swaps the active model (and downloads its files + index) without a
    // restart, mutating the shared EmbeddingOptions.Model the services above read.
    builder.Services.AddSingleton<EmbeddingModelSwitcher>();

    // MangaDex API: global limit is ~5 req/s per IP. Page image hosts
    // (at-home CDN nodes) are separate and get their own client below.
    var mangaDexLimiter = RateLimitingHandler.TokenBucket(4, TimeSpan.FromSeconds(1), burst: 4);
    builder.Services.AddHttpClient(MangaDexSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.mangadex.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/Maki)");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaDexLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    builder.Services.AddHttpClient(PageDownloader.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/Maki)");
        client.Timeout = TimeSpan.FromMinutes(2);
    });

    // Scraped sites get a conservative 1 req/s each; a real browser UA avoids
    // trivial bot filtering on plain-HTML sites.
    const string browserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    foreach (var (name, baseUrl) in new[]
             {
                 (MangaPillSource.HttpClientName, "https://mangapill.com/"),
                 (WeebCentralSource.HttpClientName, "https://weebcentral.com/"),
                 // Flame Comics — Next.js pages read for their embedded __NEXT_DATA__ props.
                 (FlameComicsSource.HttpClientName, "https://flamecomics.xyz/"),
                 // MangaKatana — SSR-rendered, no Cloudflare.
                 (MangaKatanaSource.HttpClientName, "https://mangakatana.com/")
             })
    {
        var limiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 2);
        builder.Services.AddHttpClient(name, client =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler(() => new RateLimitingHandler(limiter))
            .AddHttpMessageHandler(() => new RateLimitDetectingHandler());
    }

    var topManhuaLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 2);
    builder.Services.AddHttpClient(TopManhuaSource.HttpClientName, client =>
    {
        client.BaseAddress = new Uri("https://www.topmanhua.fan/");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.topmanhua.fan/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddHttpMessageHandler(() => new RateLimitingHandler(topManhuaLimiter))
    .AddHttpMessageHandler(() =>  new RateLimitDetectingHandler());

    // WEBTOON — plain HTML. Episode lists page 10 at a time with no bulk endpoint, so a
    // long series is dozens of requests; 2/s keeps a full chapter sync tolerable. The
    // consent/age cookies are what the site's own gate sets, and without them mature
    // titles serve an interstitial instead of the episode list.
    var webtoonsLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 4);
    builder.Services.AddHttpClient(WebtoonsSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://www.webtoons.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.webtoons.com/");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Cookie", "needGDPR=false; needCCPA=false; needCOPPA=false; ageGatePass=true");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(webtoonsLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // TCB Scans — plain HTML, English-only; wants a Referer on every request.
    var tcbLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 2);
    builder.Services.AddHttpClient(TCBScansSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://tcbonepiecechapters.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Referrer = new Uri("https://tcbonepiecechapters.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(tcbLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // Asura Scans — JSON API; the API checks Origin/Referer against the site.
    var asuraLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(AsuraSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.asurascans.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://asurascans.com");
            client.DefaultRequestHeaders.Referrer = new Uri("https://asurascans.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(asuraLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // Atsumaru — JSON API behind the site's own origin (/api), no challenge to solve. Its
    // search index is Typesense and answers straight from this client too.
    var atsumaruLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(AtsumaruSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://atsu.moe/api/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Referrer = new Uri("https://atsu.moe/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(atsumaruLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // MANGA Plus — official web API with ?format=json. It rejects requests without a
    // device secret in the Session-Token header ("Account Banned"); the app generates
    // this client-side, so one random per-process value is enough. Bans datacenter IPs.
    var mangaPlusToken = Convert.ToHexString(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    var mangaPlusLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(MangaPlusSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://jumpg-webapi.tokyo-cdn.com/api/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("okhttp/4.9.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Session-Token", mangaPlusToken);
            client.Timeout = TimeSpan.FromSeconds(60);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaPlusLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    var challengeLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 2);
    builder.Services.AddHttpClient(ChallengeAwareFetcher.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(challengeLimiter))
        // 429 only: Cloudflare answers challenges with 503, and ChallengeAwareFetcher must still
        // see that itself to hand off to FlareSolverr.
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler(treat503AsRateLimit: false));

    builder.Services.AddHttpClient(FlareSolverrClient.HttpClientName, client =>
        client.Timeout = TimeSpan.FromSeconds(90)); // FS solves can take a while

    builder.Services.AddSingleton<SettingsService>();
    builder.Services.AddSingleton<IAppSettings>(sp => sp.GetRequiredService<SettingsService>());

    // Per-user settings come in two shapes: scoped "mine" for controllers, and a singleton
    // "anybody's" for the background paths that walk several users (the scrobble tick) and for the
    // trackers, which need one user's Kitsu credentials or MangaBaka token.
    builder.Services.AddScoped<IUserSettings, UserSettingsService>();
    builder.Services.AddSingleton<IUserSettingsStore, UserSettingsStoreService>();

    builder.Services.AddSingleton<KavitaUserResolver>();
    builder.Services.AddSingleton<FlareSolverrClient>();
    builder.Services.AddSingleton<ChallengeAwareFetcher>();

    builder.Services.AddSingleton<MangaFireBrowser>();
    builder.Services.AddSingleton<TopManhuaImageBrowser>();
    builder.Services.AddSingleton<ISource, MangaDexSource>();
    builder.Services.AddSingleton<ISource, TCBScansSource>();
    builder.Services.AddSingleton<ISource, AsuraSource>();
    builder.Services.AddSingleton<ISource, WebtoonsSource>();
    builder.Services.AddSingleton<ISource, FlameComicsSource>();
    builder.Services.AddSingleton<ISource, MangaPlusSource>();
    builder.Services.AddSingleton<ISource, MangaFireSource>();
    builder.Services.AddSingleton<ISource, MangaPillSource>();
    builder.Services.AddSingleton<ISource, WeebCentralSource>();
    builder.Services.AddSingleton<ISource, MangaKatanaSource>();
    builder.Services.AddSingleton<ISource, TopManhuaSource>();
    builder.Services.AddSingleton<ISource, AtsumaruSource>();
    
    builder.Services.AddSingleton<SourceRegistry>();
    builder.Services.AddSingleton<SourceAvailability>();
    builder.Services.AddSingleton<PageDownloader>();
    builder.Services.AddSingleton<EventBroadcaster>();

    // Outbound notifications ("Connect"): user-defined connections fire on events. Providers
    // share one named HttpClient with a transient retry; new provider types are additive.
    builder.Services.AddHttpClient(DiscordNotificationProvider.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(15))
        .AddHttpMessageHandler(() => new TransientRetryHandler());
    // Lets Discord embeds upload the series poster with the message — a mediacover URL would be
    // unreachable from Discord's CDN on a self-hosted instance.
    builder.Services.AddSingleton<INotificationCoverStore>(sp => sp.GetRequiredService<CoverService>());
    builder.Services.AddSingleton<INotificationProvider, DiscordNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, WebhookNotificationProvider>();
    builder.Services.AddSingleton<NotificationService>();

    // In-app notifications: a separate, per-user pipeline. Singletons for the same reason
    // NotificationService is one — the raise sites are jobs, hosted services and other singletons.
    builder.Services.AddSingleton<InboxAudienceResolver>();
    builder.Services.AddSingleton<InboxService>();

    builder.Services.AddHttpClient(UpdateCheckService.HttpClientName, client =>
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(15);
    });
    builder.Services.AddSingleton<UpdateCheckService>();
    builder.Services.AddSingleton<HealthState>();
    builder.Services.AddScoped<HealthCheckService>();

    builder.Services.AddSingleton(TimeProvider.System);
    // Singleton on purpose: the point is that every concurrent resolve for one series shares a
    // single chapter listing. A scoped one would be per-request and cache nothing across a batch.
    builder.Services.AddSingleton<SourceChapterListCache>();
    builder.Services.AddSingleton<ChapterSourceResolver>();
    builder.Services.AddSingleton<DownloadQueueService>();
    builder.Services.AddSingleton<DownloadBatchNotifier>();
    builder.Services.AddSingleton<IDownloadCooldown>(sp => sp.GetRequiredService<DownloadQueueService>());
    builder.Services.AddScoped<ChapterSyncService>();
    builder.Services.AddScoped<SourceMatchService>();
    builder.Services.AddSingleton<SourceMatchQueue>();
    builder.Services.AddHostedService<SourceMatchWorkerHostedService>();
    builder.Services.AddScoped<ChapterDownloadProcessor>();
    builder.Services.AddScoped<LibraryImportService>();
    builder.Services.AddScoped<CbzLinkService>();
    builder.Services.AddScoped<SeriesCreationService>();
    builder.Services.AddScoped<SeriesMetadataRefreshService>();
    builder.Services.AddScoped<ReleaseService>();
    builder.Services.AddScoped<StatsEventService>();
    builder.Services.AddScoped<StatsBackfillService>();
    builder.Services.AddScoped<SeriesIdentityService>();
    builder.Services.AddScoped<SeriesIdentityRepairService>();
    builder.Services.AddScoped<ActivityStatsService>();
    builder.Services.AddScoped<UserViewResolver>();
    builder.Services.AddScoped<LibraryCompositionService>();
    // Backs UserMetricsService's short-lived snapshot cache. The metrics are recomputed from the
    // event log rather than incremented, so an entry going stale costs a badge appearing a minute
    // late and nothing else.
    builder.Services.AddMemoryCache();
    builder.Services.AddScoped<UserMetricsService>();
    builder.Services.AddScoped<AchievementService>();
    builder.Services.AddSingleton<ReadingProgressGate>();
    builder.Services.AddScoped<ReadingProgressService>();
    builder.Services.AddSingleton<ReaderArchiveCache>();
    builder.Services.AddSingleton<KavitaProgressPusher>();
    // Singleton like the two Kavita services that use it: it opens its own scope per call, so it
    // can be reached from both the import's background task and the scrobble job.
    builder.Services.AddSingleton<ExternalReadSyncService>();
    builder.Services.AddSingleton<KavitaReadImportService>();
    builder.Services.AddScoped<ReaderService>();
    builder.Services.AddScoped<ContinueReadingService>();
    builder.Services.AddScoped<ReadingProfileService>();
    builder.Services.AddScoped<OpdsCatalogService>();
    builder.Services.AddScoped<OpdsAccessService>();

    builder.Services.AddHttpClient(Maki.Core.Indexers.ProwlarrClient.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(100)) // aggregated searches fan out to indexers
        .AddHttpMessageHandler(() => new TransientRetryHandler());
    builder.Services.AddSingleton<Maki.Core.Indexers.ProwlarrClient>();
    builder.Services.AddSingleton<Maki.Core.Download.QBittorrentClient>();
    builder.Services.AddHostedService<DownloadWorkerHostedService>();

    builder.Services.AddHttpClient(Maki.Core.Kavita.KavitaClient.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(30))
        .AddHttpMessageHandler(() => new TransientRetryHandler());
    builder.Services.AddSingleton<Maki.Core.Kavita.KavitaClient>();
    builder.Services.AddSingleton<KavitaScanService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<KavitaScanService>());

    // Scrobbling: Kavita reading progress → AniList / MyAnimeList / MangaBaka.
    // Tracker endpoints are env-overridable so E2E tests can point at mocks.
    builder.Services.AddHttpClient(Maki.Core.Scrobbling.AniListTracker.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/Maki)");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new TransientRetryHandler());
    builder.Services.AddSingleton(new Maki.Core.Scrobbling.ScrobbleTrackerOptions(
        AniListApiUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_ANILIST_API") ?? "https://graphql.anilist.co",
        AniListOAuthUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_ANILIST_OAUTH") ?? "https://anilist.co/api/v2/oauth",
        MalApiUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_MAL_API") ?? "https://api.myanimelist.net/v2",
        MalOAuthUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_MAL_OAUTH") ?? "https://myanimelist.net/v1/oauth2",
        MangaBakaApiUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_MANGABAKA_API") ?? "https://api.mangabaka.org",
        KitsuApiUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_KITSU_API") ?? "https://kitsu.app/api/edge",
        KitsuOAuthUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_KITSU_OAUTH") ?? "https://kitsu.app/api/oauth"));
    builder.Services.AddSingleton<Maki.Core.Scrobbling.IScrobbleTokenStore, ScrobbleTokenStore>();
    builder.Services.AddSingleton<Maki.Core.Scrobbling.AniListTracker>();
    builder.Services.AddSingleton<Maki.Core.Scrobbling.MalTracker>();
    builder.Services.AddSingleton<Maki.Core.Scrobbling.MangaBakaTracker>();
    builder.Services.AddSingleton<Maki.Core.Scrobbling.KitsuTracker>();
    builder.Services.AddSingleton<ScrobbleService>();

    // Read before the host is built, unlike the rest of auth.*, because whether the OpenID Connect
    // scheme is registered at all is decided here. See OidcRuntimeOptions.Load.
    var oidcOptions = new OidcRuntimeOptions();
    oidcOptions.Load(paths.DatabasePath);

    builder.Services.AddMakiAuth(paths, oidcOptions);

    builder.Services.AddControllers(o =>
        {
            // CSRF for cookie-authenticated mutations. A global filter rather than an attribute per
            // action: forgetting it on one new endpoint is the whole vulnerability. Requests
            // authenticated by an API key are skipped inside the filter — a header credential is
            // never sent ambiently by a browser, so there is nothing to forge.
            o.Filters.Add<AntiforgeryCookieFilter>();

            // By default a null ObjectResult value is rewritten to a bare 204 No Content,
            // collapsing "null" into "no body" — e.g. the MAL reviews endpoint returns null to
            // mean "fetch failed" (distinct from []), and the 204 rewrite lost that signal.
            var noContentFormatter = o.OutputFormatters.OfType<HttpNoContentOutputFormatter>().FirstOrDefault();
            if (noContentFormatter is not null)
            {
                noContentFormatter.TreatNullValueAsNoContent = false;
            }
        })
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            o.JsonSerializerOptions.Converters.Add(new Maki.Api.Json.UtcDateTimeConverter());
            o.JsonSerializerOptions.Converters.Add(new Maki.Api.Json.UtcNullableDateTimeConverter());
        });
    builder.Services.AddSignalR();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddQuartz(q =>
    {
        q.ScheduleJob<Maki.Api.Jobs.RefreshMonitoredSeriesJob>(t => t
            .WithIdentity("refresh-monitored")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(5))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(30).RepeatForever()));

        q.ScheduleJob<Maki.Api.Jobs.MetadataRefreshJob>(t => t
            .WithIdentity("metadata-refresh")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(15))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        q.ScheduleJob<Maki.Api.Jobs.HousekeepingJob>(t => t
            .WithIdentity("housekeeping")
            .StartAt(DateTimeOffset.UtcNow.AddHours(1))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        q.ScheduleJob<Maki.Api.Jobs.HealthCheckJob>(t => t
            .WithIdentity("health-check")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(10))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(15).RepeatForever()));

        q.ScheduleJob<Maki.Api.Jobs.CompletedDownloadJob>(t => t
            .WithIdentity("completed-downloads")
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(15))
            .WithSimpleSchedule(s => s.WithIntervalInSeconds(15).RepeatForever()));

        q.ScheduleJob<Maki.Api.Jobs.RetryFailedDownloadsJob>(t => t
            .WithIdentity("retry-failed-downloads")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(2))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

        // Five-minute tick, independent of the scrobble sync so this works with the built-in
        // reader alone (no Kavita, no tracker configured). Topping up a queue of chapters somebody
        // is still reading through has no use for minute precision, and the job itself early-outs
        // when nothing is Smart-monitored.
        q.AddJob<Maki.Api.Jobs.SmartDownloadJob>(t => t
            .WithIdentity(Maki.Api.Jobs.SmartDownloadJob.Key)
            .StoreDurably());
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.SmartDownloadJob.Key)
            .WithIdentity("smart-download-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

        // Every-minute tick; ScrobbleService decides whether the configured interval
        // has elapsed, so interval changes apply without a restart. Stable key so the
        // sync-now endpoint can trigger it with force=true.
        q.AddJob<Maki.Api.Jobs.ScrobbleJob>(j => j
            .WithIdentity(Maki.Api.Jobs.ScrobbleJob.Key)
            .SetJobData(new JobDataMap { { Maki.Api.Jobs.ScrobbleJob.ForceKey, false } }));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.ScrobbleJob.Key)
            .WithIdentity("scrobble-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(3))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

        // Stable job key so the settings endpoint can trigger a refresh on demand.
        q.AddJob<Maki.Api.Jobs.MangaBakaDumpRefreshJob>(j => j
            .WithIdentity(Maki.Api.Jobs.MangaBakaDumpRefreshJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.MangaBakaDumpRefreshJob.Key)
            .WithIdentity("mangabaka-dump-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(2))
            .WithSimpleSchedule(s => s.WithIntervalInHours(6).RepeatForever()));

        // Prebuilt embedding index. Runs before the local indexer's trigger so a fresh install
        // downloads the vectors instead of spending an hour deriving them; no-ops when the
        // artifact is absent, incompatible, or older than what's installed.
        q.AddJob<Maki.Api.Jobs.PrebuiltIndexJob>(j => j
            .WithIdentity(Maki.Api.Jobs.PrebuiltIndexJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.PrebuiltIndexJob.Key)
            .WithIdentity("prebuilt-index-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(3))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        // Warms Discover's rail caches so the first visit after boot doesn't pay for the scan.
        // Also triggered on demand right after a MangaBaka dump install (see MangaBakaDumpRefreshJob).
        q.AddJob<Maki.Api.Jobs.DiscoverCacheWarmJob>(j => j
            .WithIdentity(Maki.Api.Jobs.DiscoverCacheWarmJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.DiscoverCacheWarmJob.Key)
            .WithIdentity("discover-cache-warm-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(5))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        // GitHub releases poll, daily. Stable key so settings can trigger a check on demand.
        q.AddJob<Maki.Api.Jobs.CheckForUpdatesJob>(j => j
            .WithIdentity(Maki.Api.Jobs.CheckForUpdatesJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.CheckForUpdatesJob.Key)
            .WithIdentity("check-for-updates-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));
    });
    builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

    var app = builder.Build();

    // Apply migrations + enable WAL on startup. Migrations are forward-only with no down path, so
    // snapshot the current DB *before* applying any pending migration — the recovery net for a bad
    // upgrade (by the time breakage shows, the migration has already run).
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var pending = db.Database.GetPendingMigrations().ToList();
        BackupInfo? preMigrationBackup = null;
        if (pending.Count > 0)
        {
            Log.Information("{Count} pending migration(s); taking pre-migration backup", pending.Count);
            preMigrationBackup = scope.ServiceProvider.GetRequiredService<BackupService>()
                .CreateAsync("auto", CancellationToken.None).GetAwaiter().GetResult();
        }
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        // Told after the migration, never before: the UserNotifications table is itself created by a
        // migration, so on the upgrade that introduces it there is nowhere to write this yet. Awaited
        // rather than fire-and-forget because the host is still starting and a detached task would
        // race the scope this borrows. Nobody is connected yet, so the SignalR push is a no-op and the
        // row is read at next sign-in — which is the point: it says what the safety net is called.
        if (preMigrationBackup is { } backup)
        {
            scope.ServiceProvider.GetRequiredService<InboxService>()
                .RaiseAsync(InboxEventType.BackupFinished, new InboxMessage(
                        Title: "Pre-upgrade backup taken",
                        Body: $"{backup.Name} — saved before applying {pending.Count} migration(s)",
                        Url: "/settings?tab=system&s=backup"),
                    InboxAudience.Admins)
                .GetAwaiter().GetResult();
        }

        // Seed the activity log from pre-existing data (once, marker-gated). Runs
        // before Kestrel/Quartz so live event hooks can't overlap the backfill window.
        scope.ServiceProvider.GetRequiredService<StatsBackfillService>()
            .RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        // After the backfill, so rows it just seeded are already keyed and this pass has nothing
        // left to do for them.
        scope.ServiceProvider.GetRequiredService<SeriesIdentityRepairService>()
            .RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        // The auth.* settings configure things built exactly once — the cookie's Secure policy, HSTS,
        // which proxies are trusted, the lockout thresholds — so they are read here, before the
        // pipeline below is assembled and before the options system first materializes the cookie
        // options on the first request.
        app.Services.GetRequiredService<AuthRuntimeOptions>()
            .LoadAsync(db, CancellationToken.None).GetAwaiter().GetResult();
    }

    var authOptions = app.Services.GetRequiredService<AuthRuntimeOptions>();

    if (oidcOptions.Enabled)
    {
        Log.Information("Single sign-on enabled against {Authority}{Only}{Provision}",
            oidcOptions.Authority,
            oidcOptions.OidcOnly ? "; local password login is admin-only" : string.Empty,
            oidcOptions.AutoProvision ? "; auto-provisioning is on" : string.Empty);

        if (!oidcOptions.AuthorityIsHttps)
        {
            Log.Warning("The single sign-on issuer is plain HTTP. The id_token is signed either way, "
                + "but the discovery document and signing keys are fetched in the clear — anyone who "
                + "can rewrite them chooses the key that signs your users' identities");
        }

        if (OidcRuntimeOptions.BreakGlassSet)
        {
            // Worth a line of its own: the operator has switched a security control off, and the
            // only record that they did is an environment variable nobody will think to check.
            Log.Warning("{Variable} is set — local password login is available to every account",
                OidcRuntimeOptions.BreakGlassVariable);
        }
    }

    // The OPDS catalogue carries its authentication token in the *path*, and Serilog's request
    // logging writes the path (never the query string) to the console and the rolling log file.
    // Every other secret Maki accepts travels as a header or a query parameter and so never
    // reaches a log; letting OPDS requests through the default pipeline would quietly turn the
    // log directory into credential material. They are dropped below the minimum level instead,
    // and OpdsController logs its own redacted line for the case worth debugging (a rejection).
    // Only honour X-Forwarded-* from proxies the operator has named. Trusting them unconditionally
    // would let any client claim any source address, which forges the audit log's ClientIp and
    // defeats the per-address rate limiter and account lockout. Without this configured, the app
    // sees the proxy's own address — wrong, but wrong in a way that cannot be attacker-controlled.
    if (authOptions.TrustedProxies.Count > 0)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        forwarded.KnownProxies.Clear();
        forwarded.KnownNetworks.Clear();
        foreach (var entry in authOptions.TrustedProxies)
        {
            if (entry.Contains('/'))
            {
                var parts = entry.Split('/', 2);
                if (IPAddress.TryParse(parts[0], out var network) && int.TryParse(parts[1], out var prefix))
                {
                    forwarded.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network, prefix));
                }
            }
            else if (IPAddress.TryParse(entry, out var proxy))
            {
                forwarded.KnownProxies.Add(proxy);
            }
        }
        app.UseForwardedHeaders(forwarded);
    }

    app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
        ctx.Request.Path.StartsWithSegments("/api/v1/opds")
            ? Serilog.Events.LogEventLevel.Verbose
            : ex is not null || ctx.Response.StatusCode > 499
                ? Serilog.Events.LogEventLevel.Error
                : Serilog.Events.LogEventLevel.Information);

    app.UseMiddleware<SecurityHeadersMiddleware>();

    if (authOptions.RequireHttps)
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    // Before authentication, and that ordering is load-bearing.
    //
    // wwwroot holds only the built SPA — its JavaScript, CSS and icons. None of it is library data;
    // the covers and pages come from controllers. Served after UseAuthorization it would be behind
    // the fail-closed fallback policy, and *not* because a matched endpoint demanded it: a request
    // for /assets/index-*.js matches no endpoint at all (MapFallbackToFile's route pattern is
    // {*path:nonfile}, which excludes anything with a file extension), and the authorization
    // middleware applies the fallback policy to endpoint-less requests too. Every script and
    // stylesheet then answers 401 to a signed-out browser, so the login page loads its shell and
    // renders nothing at all — a blank screen with no way to sign in and nothing in the log but a
    // row of 401s. Only a deployment serving the SPA from wwwroot sees it; behind the Vite dev
    // server, which serves its own assets, everything looks fine.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseRateLimiter();

    app.UseAuthentication();
    // Between authentication and authorization on purpose: this resolves the principal into the
    // database-backed CurrentUserContext that the permission handler reads, and rejects a session
    // whose account has since been disabled or deleted.
    app.UseMiddleware<CurrentUserMiddleware>();
    app.UseAuthorization();
    app.UseMiddleware<AntiforgeryTokenMiddleware>();

    // Swagger documents every endpoint in the app including the ones that replace config.json and
    // the database. It stays available in Development and is otherwise off — it is not /api-prefixed,
    // so it was never covered by the old key check and was reachable anonymously on every instance.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.MapControllers();
    app.MapHub<EventsHub>("/signalr/events");

    // Pre-authentication bootstrap for the SPA. Carries no secret: it used to hand the instance API
    // key to any anonymous caller, which made the key check decorative — anyone who could reach the
    // page could read the credential that guarded it.
    app.MapGet("/initialize.json", async (MakiDbContext db, CancellationToken ct) => Results.Json(new
    {
        apiRoot = "/api/v1",
        version = VersionInfo.Version,
        // True while the placeholder account the migration created is unclaimed, which is what sends
        // both a fresh install and an upgraded single-user one through first-run setup.
        setupNeeded = await db.Users.AnyAsync(u => u.PendingSetup, ct),
        // Enough for the login page to draw itself and no more: whether to offer the button, what to
        // write on it, and whether the password form is admin-only. The authority, client id and
        // secret stay behind the admin settings endpoint.
        oidc = new
        {
            enabled = oidcOptions.Enabled,
            displayName = oidcOptions.DisplayName,
            localLoginRestricted = oidcOptions.OidcOnly
        }
    })).AllowAnonymous();

    // The SPA shell itself must stay anonymous, or the login page can never load — MapFallbackToFile
    // registers a real endpoint, so the authorization fallback policy would otherwise 401 every deep
    // link (/library, /login) on a fresh browser. index.html carries no data; the app fetches
    // /auth/me and routes itself to the login screen on a 401.
    app.MapFallbackToFile("index.html").AllowAnonymous();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Maki terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
