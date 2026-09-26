using Jeffijoe.MessageFormat;
using Maki.Api;
using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Logging;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Inbox;
using Maki.Core.Metadata;
using Maki.Core.Notifications;
using Maki.Core.Recommendations;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.Taste;
using Maki.Metadata.RecoGraph;
using Maki.Metadata.ReaderCohorts;
using Maki.Core.Configuration;
using Maki.Sources.Asura;
using Maki.Sources.BaoziManhua;
using Maki.Sources.Common;
using Maki.Sources.Atsumaru;
using Maki.Sources.FlameComics;
using Maki.Sources.MangaLivre;
using Maki.Sources.ManhwaWeb;
using Maki.Sources.Manhwa18Net;
using Maki.Sources.SenManga;
using Maki.Sources.Shinigami;
using Maki.Sources.MangaDex;
using Maki.Sources.MangaFire;
using Maki.Sources.MangaKatana;
using Maki.Sources.Mangakakalot;
using Maki.Sources.MangaPill;
using Maki.Sources.MangaPlus;
using Maki.Sources.TCBScans;
using Maki.Sources.Toonily;
using Maki.Sources.WeebCentral;
using Maki.Sources.Webtoons;
using Maki.Sources.GigaViewer;
using Maki.Sources.Olympus;
using System.Net;
using Maki.Sources.TopManhua;
using Maki.Sources.MangaLib;
using Maki.Sources.Dynasty;
using Maki.Sources.AnimeSama;
using Maki.Sources.CuuTruyen;
using Maki.Sources.MangaWorld;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Serilog;

var paths = new AppPaths();

// Bring logging up on defaults first, so the restore below and anything else that runs before the
// host lands in the log file rather than on the console alone. The configured options are not
// knowable yet: a staged restore can replace config.json itself.
MakiLogging.Bootstrap(paths);

// Apply a restore staged by a previous run before anything reads config.json or opens the DB.
RestoreBootstrap.ApplyPendingRestore(paths, MakiLogging.CreateLogger("Restore"));

var configFile = new ConfigFileProvider(paths);
var loggingOptions = LoggingOptions.From(configFile.Config);
MakiLogging.Configure(paths, loggingOptions);

var startupLog = MakiLogging.CreateLogger("Startup");

// ImageSharp's default allocator pools every buffer it hands out and never gives one back to the
// OS, so RSS ratcheted to the high-water mark of whatever burst of concurrent decodes happened
// last - a download night or a health scan - and stayed there for the life of the process. Capping
// the pool means anything above it is an ordinary managed allocation the GC can reclaim. This is a
// retention limit, not an allocation limit: a page larger than the pool still decodes, it just is
// not kept afterwards.
SixLabors.ImageSharp.Configuration.Default.MemoryAllocator =
    SixLabors.ImageSharp.Memory.MemoryAllocator.Create(
        new SixLabors.ImageSharp.Memory.MemoryAllocatorOptions { MaximumPoolSizeMegabytes = 48 });

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();
    builder.WebHost.UseUrls($"http://*:{configFile.Config.Port}");

    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(configFile);

    // No Cache=Shared. Shared-cache mode puts every pooled connection in this process behind one
    // cache with table-level locks and returns SQLITE_LOCKED, which Microsoft.Data.Sqlite retry-spins
    // on until the command timeout. A background job writing Chapters therefore stalled unrelated API
    // reads for seconds at a time - the exact contention WAL (enabled below) exists to avoid.
    builder.Services.AddDbContext<MakiDbContext>(options =>
        options.UseSqlite($"Data Source={paths.DatabasePath}"));

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
    builder.Services.AddSingleton<MangaBakaDumpStatus>();
    builder.Services.AddSingleton<MangaBakaDumpService>();
    builder.Services.AddSingleton<MangaBakaLocalStore>();
    // Credits and the title-index term dictionary, both RAM-resident and both built lazily from the
    // dump. They are what answer "junji ito" and what let a misspelled title still find its series;
    // DiscoverCacheWarmJob builds them so the cost never lands on a keystroke.
    builder.Services.AddSingleton<CatalogueIndexCache>();
    builder.Services.AddSingleton(SearchTuning.Default.Catalogue);
    builder.Services.AddSingleton<IMetadataProvider, MangaBakaProvider>();
    builder.Services.AddSingleton<CoverService>();
    // Seed weights derived from reading behaviour, for the seeds a user never rated. Same shape as
    // SearchTuning below: the constants live in one record so distribution/eval-reco.cs can sweep
    // them, and nothing changes them at runtime.
    //
    // TasteTuning, NOT TasteVectorTuning. They are unrelated - this one weights the seeds, the other
    // is the behavioural channel and registers with its artifact further down. Renaming the second
    // out of the way of the first once took this line with it, and nothing caught it: every test
    // constructs its services by hand, so the only symptom was that the host would not start.
    builder.Services.AddSingleton(TasteTuning.Default);
    builder.Services.AddSingleton<BehavioralTasteService>();
    // The co-recommendation graph: which series readers of a given series also read, aggregated
    // from AniList and MyAnimeList. Optional — with no artifact installed the cache hands back null
    // and the channel contributes nothing, which is the state every install starts in.
    builder.Services.AddSingleton(new RecoGraphOptions(paths.RecoGraphDbPath, paths.CacheDir));
    builder.Services.AddSingleton<RecoGraphCache>();
    builder.Services.AddSingleton(RecoGraphTuning.Default);

    // The recommender's two non-channel knobs. Registered next to the channel tunings and for the
    // same reason: distribution/eval-reco-labels.cs sweeps them, and a constant buried in the class
    // is a constant nobody measures.
    builder.Services.AddSingleton(RecommenderTuning.Default);

    // ~1 MB compressed, but on the same slow-line budget as the index download: the cost of a
    // generous timeout is a job that finishes late, the cost of a tight one is a channel that
    // never installs on a connection that would have managed it.
    builder.Services.AddHttpClient(RecoGraphInstaller.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.Timeout = TimeSpan.FromMinutes(10);
    });
    builder.Services.AddSingleton<RecoGraphInstaller>();

    // Co-read graph: the same shape of artifact from a different crowd, installed independently.
    builder.Services.AddSingleton(new CoReadOptions(paths.CoReadDbPath, paths.CacheDir));
    builder.Services.AddSingleton<CoReadCache>();
    builder.Services.AddSingleton(CoReadTuning.Default);

    // Behavioural vectors. Registered before VectorIndexCache resolves, because the cache loads the
    // artifact as part of building the index rather than keeping a cache of its own.
    builder.Services.AddSingleton(new TasteVectorOptions(paths.TasteVectorsDbPath, paths.CacheDir));
    builder.Services.AddSingleton(TasteVectorTuning.Default);
    builder.Services.AddHttpClient(TasteVectorInstaller.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton<TasteVectorInstaller>();

    // ~16 MB compressed against the vote graph's ~1 MB, so the same generous timeout matters more
    // here: the cost of a long one is a job that finishes late, the cost of a tight one is a
    // channel that never installs on a connection that would have managed it.
    builder.Services.AddHttpClient(CoReadInstaller.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton<CoReadInstaller>();

    // Reader cohorts. Its own cache rather than a layer inside VectorIndexCache, because nothing
    // here is scanned per catalogue row: placement is one lookup per series the reader finished.
    // That is what lets the file be swapped under a running process without invalidating the index.
    builder.Services.AddSingleton(new ReaderCohortOptions(paths.ReaderCohortsDbPath, paths.CacheDir));
    builder.Services.AddSingleton(ReaderCohortTuning.Default);
    builder.Services.AddSingleton<ReaderCohortCache>();
    builder.Services.AddHttpClient(ReaderCohortInstaller.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton<ReaderCohortInstaller>();

    builder.Services.AddSingleton<SeedWeightService>();
    builder.Services.AddScoped<RecommendationFeedbackService>();
    builder.Services.AddHostedService<RecommendationFeedbackPruneService>();
    builder.Services.AddSingleton<RecommendationService>();
    builder.Services.AddSingleton<RecentActivityRailService>();
    builder.Services.AddSingleton<SideInterestRailService>();
    builder.Services.AddSingleton<ReaderCohortService>();
    builder.Services.AddSingleton<ReaderCohortRailService>();
    builder.Services.AddSingleton<TasteProfileService>();
    builder.Services.AddSingleton<TasteInsightsService>();
    builder.Services.AddSingleton<TasteAvoidanceService>();
    builder.Services.AddSingleton<ReadingBehaviourService>();
    builder.Services.AddSingleton<SimilarSeriesService>();
    builder.Services.AddSingleton<DiscoverService>();
    builder.Services.AddScoped<HiddenContentService>();
    builder.Services.AddScoped<CustomRailService>();

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
    // Reads the GC, the process and the kernel's cgroup accounting for GET system/memory. Holds
    // no state of its own; a singleton only because everything it inspects is one.
    builder.Services.AddSingleton<MemoryDiagnostics>();
    builder.Services.AddSingleton<Maki.Api.Jobs.ArtifactBuildGate>();
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
                 (MangaKatanaSource.HttpClientName, "https://mangakatana.com/"),
                 // GigaViewer sites (Hatena's white-label viewer, plain nginx/CloudFront, no
                 // challenge). Page images go through their own client below.
                 ($"source-{GigaViewerSites.ShonenJumpPlus.Name}", $"{GigaViewerSites.ShonenJumpPlus.BaseUrl}/"),
                 ($"source-{GigaViewerSites.ComicDays.Name}", $"{GigaViewerSites.ComicDays.BaseUrl}/"),
                 ($"source-{GigaViewerSites.SundayWebry.Name}", $"{GigaViewerSites.SundayWebry.BaseUrl}/"),
                 ($"source-{GigaViewerSites.Magcomi.Name}", $"{GigaViewerSites.Magcomi.BaseUrl}/"),
                 ($"source-{GigaViewerSites.TonarinoYj.Name}", $"{GigaViewerSites.TonarinoYj.BaseUrl}/"),
                 ($"source-{GigaViewerSites.ComicZenon.Name}", $"{GigaViewerSites.ComicZenon.BaseUrl}/"),
                 ($"source-{GigaViewerSites.KurageBunch.Name}", $"{GigaViewerSites.KurageBunch.BaseUrl}/"),
                 // Dynasty Scans — plain nginx, no Cloudflare.
                 (DynastySource.HttpClientName, "https://dynasty-scans.com/"),
                 // ManhwaWeb: separate JSON API host, no Cloudflare in front of it.
                 (ManhwaWebSource.HttpClientName, ManhwaWebSource.ApiUrl + "/")
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

    // GigaViewer page images: fetched and descrambled one at a time inside GetPagesAsync
    // (Data hatch), so a slightly higher rate than the 1 req/s HTML clients is fine.
    var gigaViewerImageLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 4);
    builder.Services.AddHttpClient(GigaViewerSource.ImageHttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(gigaViewerImageLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // Shinigami: the website (numbered subdomain) is Cloudflare-challenged, but its JSON API
    // (api.shngm.io) answers plain HTTP with no challenge, so this client's base address is the
    // API host, not BaseUrl. Both are separately env-overridable since the site's leading number
    // rotates independently of the API host.
    var shinigamiApiUrl = Environment.GetEnvironmentVariable("MAKI_SOURCE_SHINIGAMI_APIURL")?.TrimEnd('/')
        ?? "https://api.shngm.io";
    var shinigamiLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 2);
    builder.Services.AddHttpClient(ShinigamiSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri($"{shinigamiApiUrl}/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(shinigamiLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

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

    // Sen Manga — Japanese raw scans; a client-rendered SPA whose own JSON API is called directly.
    var senMangaLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(SenMangaSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://raw.senmanga.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Referrer = new Uri("https://raw.senmanga.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(senMangaLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // Baozi Manhua — Simplified Chinese manhua, plain SSR/AMP HTML, no Cloudflare.
    var baoziLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(BaoziManhuaSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://cn.baozimh.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Referrer = new Uri("https://cn.baozimh.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(baoziLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // Manga Livre — Brazilian Portuguese, standard Madara/WordPress theme, no Cloudflare.
    var mangaLivreLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(MangaLivreSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://mangalivre.to/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Referrer = new Uri("https://mangalivre.to/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaLivreLimiter))
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

    // MANGA Plus — the protobuf API the site's own SPA calls (?format=json now 403s at the
    // edge). Without a SESSION-TOKEN header it answers 200 with an "Account Banned" popup; the
    // site generates a UUID client-side and keeps it in localStorage, so one random per-process
    // value is enough. Bans datacenter IPs.
    var mangaPlusToken = Guid.NewGuid().ToString();
    var mangaPlusLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 3);
    builder.Services.AddHttpClient(MangaPlusSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://jumpg-webapi.tokyo-cdn.com/api/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.TryAddWithoutValidation("SESSION-TOKEN", mangaPlusToken);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://mangaplus.shueisha.co.jp");
            client.DefaultRequestHeaders.Referrer = new Uri("https://mangaplus.shueisha.co.jp/");
            client.Timeout = TimeSpan.FromSeconds(60);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaPlusLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    // MangaLib (LibGroup): JSON API behind DDoS-Guard, not Cloudflare; answers a plain client
    // 200 as long as every request carries a mangalib Referer (403 without it). The API host
    // rotates (Keiyoushi exposes a picker), so MAKI_SOURCE_MANGALIB_APIURL overrides the default.
    var mangaLibApiUrl = (Environment.GetEnvironmentVariable("MAKI_SOURCE_MANGALIB_APIURL") ?? "https://api.cdnlibs.org").TrimEnd('/');
    var mangaLibLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 2);
    builder.Services.AddHttpClient(MangaLibSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri($"{mangaLibApiUrl}/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.DefaultRequestHeaders.Referrer = new Uri("https://mangalib.me/");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Site-Id", "1");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaLibLimiter))
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

    // CuuTruyen: the API side goes through IHtmlFetcher, but page bytes are
    // binary and need unscrambling before they reach the downloader, so this client fetches raw
    // images only. No BaseAddress: URLs already point at whichever storage-* host the page rewrite
    // picked. ~1 MB pages, so a longer timeout than the plain-HTML sources above.
    var cuuTruyenLimiter = RateLimitingHandler.TokenBucket(2, TimeSpan.FromSeconds(1), burst: 4);
    builder.Services.AddHttpClient(CuuTruyenSource.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.Timeout = TimeSpan.FromSeconds(60);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(cuuTruyenLimiter))
        .AddHttpMessageHandler(() => new RateLimitDetectingHandler());

    builder.Services.AddSingleton<SettingsService>();
    builder.Services.AddSingleton<IAppSettings>(sp => sp.GetRequiredService<SettingsService>());

    // Per-user settings come in two shapes: scoped "mine" for controllers, and a singleton
    // "anybody's" for the background paths that walk several users (the scrobble tick) and for the
    // trackers, which need one user's Kitsu credentials or MangaBaka token.
    builder.Services.AddScoped<IUserSettings, UserSettingsService>();
    builder.Services.AddSingleton<IUserSettingsStore, UserSettingsStoreService>();

    // Localization. Catalogs and the ICU formatter are immutable and shared; only the per-request
    // language and the localizer that reads it are scoped.
    //
    // Note what is NOT here: UseRequestLocalization, and any call that sets CurrentUICulture or
    // CurrentCulture. That middleware sets both, and about thirty places in this codebase parse
    // chapter numbers, file sizes and dates with InvariantCulture on purpose. An ambient German or
    // Turkish culture reinterpreting "12.5" would not fail a build and would reach the filesystem.
    // The language travels as ordinary scoped state that only the localizer reads. See IRequestLocale.
    builder.Services.AddSingleton<ServerCatalogs>();
    builder.Services.AddSingleton<IMessageFormatter>(_ => new MessageFormatter(useCache: true));
    builder.Services.AddSingleton<IUserLocaleResolver, UserLocaleResolver>();
    builder.Services.AddScoped<RequestLocaleContext>();
    builder.Services.AddScoped<IRequestLocale>(sp => sp.GetRequiredService<RequestLocaleContext>());
    builder.Services.AddSingleton<IMessageCatalog, MessageCatalog>();
    builder.Services.AddScoped<ILocalizer, Localizer>();
    // Scoped rather than singleton because it renders through the scoped ILocalizer. Both the read
    // path and the raise path resolve it from whatever scope they are already holding.
    builder.Services.AddScoped<InboxRenderer>();

    builder.Services.AddSingleton<KavitaUserResolver>();
    builder.Services.AddSingleton<FlareSolverrClient>();
    builder.Services.AddSingleton<ChallengeAwareFetcher>();
    // Sources that only need "fetch this URL past Cloudflare" take the interface, so their parsers
    // stay testable without FlareSolverr and settings in the way.
    builder.Services.AddSingleton<IHtmlFetcher>(sp => sp.GetRequiredService<ChallengeAwareFetcher>());

    builder.Services.AddSingleton<MangaFireBrowser>();
    builder.Services.AddSingleton<TopManhuaImageBrowser>();
    // Both of the above, again, as the seam BrowserIdleShutdownJob closes them through. Resolved
    // from the concrete singletons rather than registered twice, or the job would be shutting down
    // a second browser nobody scrapes with.
    builder.Services.AddSingleton<IIdleBrowser>(sp => sp.GetRequiredService<MangaFireBrowser>());
    builder.Services.AddSingleton<IIdleBrowser>(sp => sp.GetRequiredService<TopManhuaImageBrowser>());
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
    builder.Services.AddSingleton<ISource, MangakakalotSource>();
    builder.Services.AddSingleton<ISource, TopManhuaSource>();
    builder.Services.AddSingleton<ISource, AtsumaruSource>();
    builder.Services.AddSingleton<ISource, SenMangaSource>();
    builder.Services.AddSingleton<ISource, BaoziManhuaSource>();
    builder.Services.AddSingleton<ISource, MangaLivreSource>();
    builder.Services.AddSingleton<ISource, ShonenJumpPlusSource>();
    builder.Services.AddSingleton<ISource, ComicDaysSource>();
    builder.Services.AddSingleton<ISource, SundayWebrySource>();
    builder.Services.AddSingleton<ISource, MagcomiSource>();
    builder.Services.AddSingleton<ISource, TonarinoYjSource>();
    builder.Services.AddSingleton<ISource, ComicZenonSource>();
    builder.Services.AddSingleton<ISource, KurageBunchSource>();
    builder.Services.AddSingleton<ISource, ToonilySource>();
    builder.Services.AddSingleton<ISource, MangaLibSource>();
    builder.Services.AddSingleton<ISource, DynastySource>();
    builder.Services.AddSingleton<ISource, AnimeSamaSource>();
    builder.Services.AddSingleton<ISource, ManhwaWebSource>();
    builder.Services.AddSingleton<ISource, OlympusSource>();

    builder.Services.AddSingleton<ISource, ShinigamiSource>();
    builder.Services.AddSingleton<ISource, Manhwa18NetSource>();
    builder.Services.AddSingleton<ISource, CuuTruyenSource>();
    builder.Services.AddSingleton<ISource, MangaWorldSource>();

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
    builder.Services.AddSingleton<INotificationProvider, TelegramNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, NotifiarrNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, NtfyNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, GotifyNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, PushoverNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, AppriseNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, SlackWebhookNotificationProvider>();
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
    builder.Services.AddScoped<HealthMonitor>();
    builder.Services.AddSingleton<HealthSourceRecovery>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthSourceRecovery>());
    builder.Services.AddScoped<HealthScanService>();
    builder.Services.AddScoped<HealthMatchService>();
    builder.Services.AddScoped<HealthOperationService>();
    builder.Services.AddHostedService<HealthWorker>();

    builder.Services.AddSingleton(TimeProvider.System);
    // Singleton on purpose: the point is that every concurrent resolve for one series shares a
    // single chapter listing. A scoped one would be per-request and cache nothing across a batch.
    builder.Services.AddSingleton<SourceChapterListCache>();
    builder.Services.AddSingleton<SourceExternalIdCache>();
    builder.Services.AddSingleton<ChapterSourceResolver>();
    builder.Services.AddSingleton<DownloadQueueService>();
    builder.Services.AddSingleton<DownloadBatchNotifier>();
    builder.Services.AddSingleton<IDownloadCooldown>(sp => sp.GetRequiredService<DownloadQueueService>());
    builder.Services.AddScoped<ChapterSyncService>();
    builder.Services.AddScoped<SourceMappingRemovalService>();
    builder.Services.AddScoped<SourceMatchService>();
    builder.Services.AddSingleton<SourceMatchQueue>();
    // Singleton because it owns detached jobs the request that started them no longer waits on.
    builder.Services.AddSingleton<SourceComparePreviewService>();
    builder.Services.AddHostedService<SourceMatchWorkerHostedService>();
    builder.Services.AddScoped<ChapterDownloadProcessor>();
    builder.Services.AddScoped<LibraryImportService>();
    builder.Services.AddScoped<CbzLinkService>();
    builder.Services.AddScoped<SeriesCreationService>();
    builder.Services.AddScoped<NamingService>();
    builder.Services.AddScoped<SeriesRenameService>();
    builder.Services.AddScoped<TorrentImportService>();
    builder.Services.AddScoped<SeriesMetadataRefreshService>();
    builder.Services.AddScoped<ImageCacheRebuildService>();
    // Singleton: it is the single-flight claim and the live progress a rebuild reports through,
    // so it has to outlive both the request that starts one and the job scope that runs it.
    builder.Services.AddSingleton<ImageCacheRebuildStatus>();
    builder.Services.AddScoped<ReleaseService>();
    builder.Services.AddScoped<StatsEventService>();
    builder.Services.AddScoped<StatsBackfillService>();
    builder.Services.AddScoped<SeriesIdentityService>();
    builder.Services.AddScoped<SeriesIdentityRepairService>();
    builder.Services.AddScoped<ImportPathRepairService>();
    builder.Services.AddScoped<ActivityStatsService>();
    builder.Services.AddScoped<UserViewResolver>();
    builder.Services.AddScoped<LibraryCompositionService>();
    builder.Services.AddScoped<StatsInsightsService>();
    builder.Services.AddScoped<StatsStandingService>();
    builder.Services.AddScoped<ReadingSessionService>();
    builder.Services.AddScoped<ReadingSessionBackfillService>();
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
    builder.Services.AddScoped<ReadingTimeEstimateService>();
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
    // The same four instances, in ScrobbleService's order, for consumers that only need the interface.
    builder.Services.AddSingleton<Maki.Core.Scrobbling.IScrobbleTracker>(sp => sp.GetRequiredService<Maki.Core.Scrobbling.AniListTracker>());
    builder.Services.AddSingleton<Maki.Core.Scrobbling.IScrobbleTracker>(sp => sp.GetRequiredService<Maki.Core.Scrobbling.MalTracker>());
    builder.Services.AddSingleton<Maki.Core.Scrobbling.IScrobbleTracker>(sp => sp.GetRequiredService<Maki.Core.Scrobbling.MangaBakaTracker>());
    builder.Services.AddSingleton<Maki.Core.Scrobbling.IScrobbleTracker>(sp => sp.GetRequiredService<Maki.Core.Scrobbling.KitsuTracker>());
    builder.Services.AddSingleton<ImportListService>();
    builder.Services.AddScoped<SeriesRequestSubmitter>();
    builder.Services.AddSingleton<AnimeSignalSources>();
    builder.Services.AddSingleton<AnimeSignalSyncService>();
    builder.Services.AddScoped<AnimeResumeService>();

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
        // Well above the default ten. The artifact builds serialise on ArtifactBuildGate rather
        // than on the pool, so a queue of them waiting their turn each occupies a slot, and the
        // download workers and the fifteen-second completed-download poll must not be behind them.
        q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 20);
        q.AddJobListener<HealthJobListener>();
        // Twenty minutes rather than five, to keep the first source sync out of the window where
        // every index is being built. It is the one startup job that launches a headless browser
        // (MangaFire), so it used to add ~120 MB of native memory at exactly the minute the builds
        // were at their peak. Nothing needs it sooner: it repeats every half hour regardless.
        q.ScheduleJob<Maki.Api.Jobs.RefreshMonitoredSeriesJob>(t => t
            .WithIdentity("refresh-monitored")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(20))
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
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(10))
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

        // Every-minute tick; ImportListService decides whether the configured interval has elapsed.
        // Starts well after the scrobble tick so the first passes don't hit the trackers together.
        q.AddJob<Maki.Api.Jobs.ImportListJob>(j => j.WithIdentity(Maki.Api.Jobs.ImportListJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.ImportListJob.Key)
            .WithIdentity("import-lists-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(10))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

        // Hourly tick; AnimeSignalSyncService decides whether its own (much longer) interval has
        // elapsed. Stable key so the opt-in endpoint can trigger it with force=true.
        q.AddJob<Maki.Api.Jobs.AnimeSignalJob>(j => j
            .WithIdentity(Maki.Api.Jobs.AnimeSignalJob.Key)
            .SetJobData(new JobDataMap { { Maki.Api.Jobs.AnimeSignalJob.ForceKey, false } }));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.AnimeSignalJob.Key)
            .WithIdentity("anime-signal-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(5))
            .WithSimpleSchedule(s => s.WithIntervalInHours(1).RepeatForever()));

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

        // Co-recommendation graph. Last of the three staggered artifact downloads so it is not
        // competing with the dump or the index for bandwidth on a fresh install - it is the
        // smallest and the least urgent, since recommendations work without it.
        q.AddJob<Maki.Api.Jobs.RecoGraphJob>(j => j
            .WithIdentity(Maki.Api.Jobs.RecoGraphJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.RecoGraphJob.Key)
            .WithIdentity("reco-graph-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(4))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        // Co-read graph, last of the staggered artifact downloads: it is by far the largest of them
        // and the one recommendations least need, so it goes behind the dump, the index and the
        // vote graph rather than competing with them on a fresh install.
        q.AddJob<Maki.Api.Jobs.CoReadJob>(j => j
            .WithIdentity(Maki.Api.Jobs.CoReadJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.CoReadJob.Key)
            .WithIdentity("coread-graph-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(5))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        // Behavioural vectors, behind every other artifact. Installing them invalidates the vector
        // index rather than swapping a file in, so running this while the index is still building
        // for the first time would throw that work away and start it again.
        q.AddJob<Maki.Api.Jobs.TasteVectorJob>(j => j
            .WithIdentity(Maki.Api.Jobs.TasteVectorJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.TasteVectorJob.Key)
            .WithIdentity("taste-vectors-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(6))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        // Reader cohorts, behind everything else. Unlike the behavioural vectors this one swaps a
        // file in rather than invalidating the index, so it is last for bandwidth rather than for
        // correctness: the surfaces reading it are a hint and a rail.
        q.AddJob<Maki.Api.Jobs.ReaderCohortJob>(j => j
            .WithIdentity(Maki.Api.Jobs.ReaderCohortJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.ReaderCohortJob.Key)
            .WithIdentity("reader-cohorts-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(7))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        // Warms Discover's rail caches so the first visit after boot doesn't pay for the scan.
        // Also triggered on demand right after a MangaBaka dump install (see MangaBakaDumpRefreshJob).
        q.AddJob<Maki.Api.Jobs.DiscoverCacheWarmJob>(j => j
            .WithIdentity(Maki.Api.Jobs.DiscoverCacheWarmJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.DiscoverCacheWarmJob.Key)
            .WithIdentity("discover-cache-warm-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(5))
            // Twelve hours, matching DiscoverService's rail cache rather than doubling it. At
            // twenty-four one of every two expiries landed on whoever opened Discover next, and
            // they paid for a cold rebuild of every rail. It is gated behind ArtifactBuildGate, so
            // a second one cannot overlap an index build.
            .WithSimpleSchedule(s => s.WithIntervalInHours(12).RepeatForever()));

        // Frees the embedding session when nothing has used it. Five minutes is the tick, not the
        // idle window - the job reads that itself - so the window can change without rescheduling.
        q.AddJob<Maki.Api.Jobs.EmbedderIdleUnloadJob>(j => j
            .WithIdentity(Maki.Api.Jobs.EmbedderIdleUnloadJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.EmbedderIdleUnloadJob.Key)
            .WithIdentity("embedder-idle-unload-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(8))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

        // The same for the discovery artifacts, which are ~72 MB between them. Started well after
        // the warm-up job so a fresh instance is not unloading what it is still building, and the
        // tick is the same five minutes for the same reason.
        q.AddJob<Maki.Api.Jobs.ArtifactIdleUnloadJob>(j => j
            .WithIdentity(Maki.Api.Jobs.ArtifactIdleUnloadJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.ArtifactIdleUnloadJob.Key)
            .WithIdentity("artifact-idle-unload-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(12))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

        // And the headless browsers, which are ~160 MB of native memory between the Playwright
        // driver and the shell's processes. Nothing launches one at startup, so this can start
        // early; it does nothing until a scrape has actually happened.
        q.AddJob<Maki.Api.Jobs.BrowserIdleShutdownJob>(j => j
            .WithIdentity(Maki.Api.Jobs.BrowserIdleShutdownJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.BrowserIdleShutdownJob.Key)
            .WithIdentity("browser-idle-shutdown-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(6))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()));

        // Image cache rebuild. Registered with no trigger at all: it re-downloads a poster per
        // series, so it only ever runs when an admin asks for it from System settings.
        q.AddJob<Maki.Api.Jobs.ImageCacheRebuildJob>(j => j
            .WithIdentity(Maki.Api.Jobs.ImageCacheRebuildJob.Key)
            .StoreDurably());

        // GitHub releases poll, daily. Stable key so settings can trigger a check on demand.
        q.AddJob<Maki.Api.Jobs.CheckForUpdatesJob>(j => j
            .WithIdentity(Maki.Api.Jobs.CheckForUpdatesJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.CheckForUpdatesJob.Key)
            .WithIdentity("check-for-updates-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));
    });
    // WaitForJobsToComplete so an in-flight download finishes rather than being torn in half.
    // QuartzShutdownInterrupter is what keeps that from meaning "wait forever" - see its remarks.
    builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
    builder.Services.AddHostedService<QuartzShutdownInterrupter>();

    // Outbound request logging, added to every named client at once rather than to each of the
    // thirty registrations above, where one would eventually be forgotten. This replaces the four
    // Information lines per call that Microsoft.Extensions.Http writes; MakiLogging floors those
    // categories at Warning.
    //
    // Registered last on purpose. Configure actions run in registration order and each appends to
    // AdditionalHandlers, whose first entry is the outermost handler, so arriving last puts this
    // innermost: below the rate limiter, and inside the retry handler, where it sees each attempt
    // separately. A DelegatingHandler cannot be shared between two chains (its InnerHandler is set
    // once), hence the transient registration and a fresh instance per builder.
    builder.Services.AddTransient<OutboundHttpLoggingHandler>();
    builder.Services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(options =>
        options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
            handlerBuilder.AdditionalHandlers.Add(
                handlerBuilder.Services.GetRequiredService<OutboundHttpLoggingHandler>())));

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
            startupLog.LogInformation("{Count} pending migration(s); taking pre-migration backup", pending.Count);
            preMigrationBackup = scope.ServiceProvider.GetRequiredService<BackupService>()
                .CreateAsync("auto", CancellationToken.None).GetAwaiter().GetResult();
        }
        try { db.Database.Migrate(); }
        catch
        {
            try { File.WriteAllText(Path.Combine(scope.ServiceProvider.GetRequiredService<AppPaths>().ConfigDir, "health-migration-error.txt"), DateTime.UtcNow.ToString("O")); } catch { }
            throw;
        }
        scope.ServiceProvider.GetRequiredService<HealthOperationService>().RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();
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
                        Key: "inbox.backup.preUpgrade",
                        Params: InboxMessage.Args(new { name = backup.Name, count = pending.Count }),
                        Url: "/settings?tab=system&s=backup"),
                    InboxAudience.Admins)
                .GetAwaiter().GetResult();
        }

        // Seed the activity log from pre-existing data (once, marker-gated). Runs
        // before Kestrel/Quartz so live event hooks can't overlap the backfill window.
        scope.ServiceProvider.GetRequiredService<StatsBackfillService>()
            .RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Stitches historical ReadingTime events into ReadingSessions once, so sittings exist
        // on the stats page from the first release rather than only for reads after upgrade.
        scope.ServiceProvider.GetRequiredService<ReadingSessionBackfillService>()
            .RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        // After the backfill, so rows it just seeded are already keyed and this pass has nothing
        // left to do for them.
        scope.ServiceProvider.GetRequiredService<SeriesIdentityRepairService>()
            .RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        scope.ServiceProvider.GetRequiredService<ImportPathRepairService>()
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
        startupLog.LogInformation("Single sign-on enabled against {Authority}{Only}{Provision}",
            oidcOptions.Authority,
            oidcOptions.OidcOnly ? "; local password login is admin-only" : string.Empty,
            oidcOptions.AutoProvision ? "; auto-provisioning is on" : string.Empty);

        if (!oidcOptions.AuthorityIsHttps)
        {
            startupLog.LogWarning("The single sign-on issuer is plain HTTP. The id_token is signed either way, "
                + "but the discovery document and signing keys are fetched in the clear — anyone who "
                + "can rewrite them chooses the key that signs your users' identities");
        }

        if (OidcRuntimeOptions.BreakGlassSet)
        {
            // Worth a line of its own: the operator has switched a security control off, and the
            // only record that they did is an environment variable nobody will think to check.
            startupLog.LogWarning("{Variable} is set — local password login is available to every account",
                OidcRuntimeOptions.BreakGlassVariable);
        }
    }

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
        forwarded.KnownIPNetworks.Clear();
        foreach (var entry in authOptions.TrustedProxies)
        {
            if (entry.Contains('/'))
            {
                var parts = entry.Split('/', 2);
                if (IPAddress.TryParse(parts[0], out var network) && int.TryParse(parts[1], out var prefix) && prefix >= 0)
                {
                    // System.Net.IPNetwork rejects host bits and oversized prefixes that the old
                    // HttpOverrides type tolerated, so normalise rather than fail startup.
                    var bytes = network.GetAddressBytes();
                    prefix = Math.Min(prefix, bytes.Length * 8);
                    for (var bit = prefix; bit < bytes.Length * 8; bit++)
                    {
                        bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
                    }
                    forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(new IPAddress(bytes), prefix));
                }
            }
            else if (IPAddress.TryParse(entry, out var proxy))
            {
                forwarded.KnownProxies.Add(proxy);
            }
        }
        app.UseForwardedHeaders(forwarded);
    }

    // What each request is worth a line for lives in HttpRequestLogPolicy, including the rule that
    // keeps OPDS out of the log entirely: its authentication token is in the path, and request
    // logging writes paths.
    app.UseSerilogRequestLogging(o =>
    {
        o.GetLevel = HttpRequestLogPolicy.For(loggingOptions);

        // The default template opens with a literal "HTTP" (the line is already labelled Http) and
        // renders the duration to four decimal places, which is four more than anyone reads.
        o.MessageTemplate = "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0} ms";
    });

    // A browser that navigates away mid-request cancels RequestAborted, and the query awaiting it
    // throws. Left alone that reaches request logging and Kestrel as an unhandled 500.
    app.Use(async (context, next) =>
    {
        try { await next(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
    });

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
    // Before CurrentUserMiddleware, so the 401 that middleware answers with is localized too. It
    // reads nothing but the request itself (a query parameter, two headers), and the stored
    // preference that does need a user is resolved lazily on first read, by which point
    // CurrentUserMiddleware has run for every request that gets that far.
    app.UseMiddleware<RequestLocaleMiddleware>();
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
            // Empty when no admin ever typed a label, rather than the English default: an admin's
            // own words go out verbatim, but "Single sign-on" itself has to go through the request's
            // own language, and the client already carries its own translated copy for this blank case.
            displayName = oidcOptions.DisplayNameIsCustom ? oidcOptions.DisplayName : string.Empty,
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
    startupLog.LogCritical(ex, "Maki terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
