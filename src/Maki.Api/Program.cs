using Maki.Api;
using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Api.Middleware;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Metadata;
using Maki.Core.Notifications;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Core.Configuration;
using Maki.Sources.Asura;
using Maki.Sources.MangaDex;
using Maki.Sources.MangaFire;
using Maki.Sources.MangaPill;
using Maki.Sources.MangaPlus;
using Maki.Sources.TCBScans;
using Maki.Sources.WeebCentral;
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
    builder.Services.AddSingleton<IMetadataProvider, MangaBakaProvider>();
    builder.Services.AddSingleton<CoverService>();
    builder.Services.AddSingleton<RecommendationService>();
    builder.Services.AddSingleton<DiscoverService>();

    // Semantic recommendations: a local ONNX embedding model (~130 MB, downloaded on first
    // use) turns each series' description into a vector so Discover can match on "feel", not
    // just shared genre labels. The one-time index pass runs as a background job.
    builder.Services.AddHttpClient(EmbeddingModelStore.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton(new EmbeddingOptions(paths.ModelsDir, paths.EmbeddingsDbPath, paths.CacheDir));
    builder.Services.AddSingleton<EmbeddingModelStore>();
    builder.Services.AddSingleton<TextEmbedder>();
    builder.Services.AddSingleton<EmbeddingStore>();
    builder.Services.AddSingleton<EmbeddingIndexStatus>();
    builder.Services.AddSingleton<SeriesEmbeddingIndexer>();
    builder.Services.AddSingleton<SemanticRecommender>();

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
                 (WeebCentralSource.HttpClientName, "https://weebcentral.com/")
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
    builder.Services.AddSingleton<FlareSolverrClient>();
    builder.Services.AddSingleton<ChallengeAwareFetcher>();

    builder.Services.AddSingleton<ISource, MangaPlusSource>();
    builder.Services.AddSingleton<ISource, MangaFireSource>();
    builder.Services.AddSingleton<ISource, MangaDexSource>();
    builder.Services.AddSingleton<ISource, MangaPillSource>();
    builder.Services.AddSingleton<ISource, WeebCentralSource>();
    builder.Services.AddSingleton<ISource, TCBScansSource>();
    builder.Services.AddSingleton<ISource, AsuraSource>();
    builder.Services.AddSingleton<SourceRegistry>();
    builder.Services.AddSingleton<PageDownloader>();
    builder.Services.AddSingleton<EventBroadcaster>();

    // Outbound notifications ("Connect"): user-defined connections fire on events. Providers
    // share one named HttpClient with a transient retry; new provider types are additive.
    builder.Services.AddHttpClient(DiscordNotificationProvider.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(15))
        .AddHttpMessageHandler(() => new TransientRetryHandler());
    builder.Services.AddSingleton<INotificationProvider, DiscordNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, WebhookNotificationProvider>();
    builder.Services.AddSingleton<NotificationService>();

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
    builder.Services.AddSingleton<DownloadQueueService>();
    builder.Services.AddSingleton<IDownloadCooldown>(sp => sp.GetRequiredService<DownloadQueueService>());
    builder.Services.AddScoped<ChapterSyncService>();
    builder.Services.AddScoped<SourceMatchService>();
    builder.Services.AddScoped<ChapterDownloadProcessor>();
    builder.Services.AddScoped<LibraryImportService>();
    builder.Services.AddScoped<CbzLinkService>();
    builder.Services.AddScoped<SeriesMetadataRefreshService>();
    builder.Services.AddScoped<ReleaseService>();
    builder.Services.AddScoped<StatsEventService>();
    builder.Services.AddScoped<StatsBackfillService>();
    builder.Services.AddScoped<RewindService>();

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
        MangaBakaApiUrl: Environment.GetEnvironmentVariable("MAKI_SCROBBLE_MANGABAKA_API") ?? "https://api.mangabaka.org"));
    builder.Services.AddSingleton<Maki.Core.Scrobbling.IScrobbleTokenStore, ScrobbleTokenStore>();
    builder.Services.AddSingleton<Maki.Core.Scrobbling.AniListTracker>();
    builder.Services.AddSingleton<Maki.Core.Scrobbling.MalTracker>();
    builder.Services.AddSingleton<Maki.Core.Scrobbling.MangaBakaTracker>();
    builder.Services.AddSingleton<ScrobbleService>();

    builder.Services.AddControllers(o =>
        {
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
            o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
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

        // Embedding index for semantic recommendations. Starts a few minutes after boot (giving
        // the dump time to land on first run) and refreshes daily; skips unchanged series so
        // repeat runs are cheap. Stable key so it can be triggered on demand.
        q.AddJob<Maki.Api.Jobs.EmbeddingIndexJob>(j => j
            .WithIdentity(Maki.Api.Jobs.EmbeddingIndexJob.Key));
        q.AddTrigger(t => t
            .ForJob(Maki.Api.Jobs.EmbeddingIndexJob.Key)
            .WithIdentity("embedding-index-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(4))
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
        if (pending.Count > 0)
        {
            Log.Information("{Count} pending migration(s); taking pre-migration backup", pending.Count);
            scope.ServiceProvider.GetRequiredService<BackupService>()
                .CreateAsync("auto", CancellationToken.None).GetAwaiter().GetResult();
        }
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        // Seed the Rewind activity log from pre-existing data (once, marker-gated). Runs
        // before Kestrel/Quartz so live event hooks can't overlap the backfill window.
        scope.ServiceProvider.GetRequiredService<StatsBackfillService>()
            .RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    app.UseSerilogRequestLogging();
    app.UseMiddleware<ApiKeyMiddleware>();

    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapControllers();
    app.MapHub<EventsHub>("/signalr/events");
    app.MapGet("/initialize.json", (ConfigFileProvider cfg) => Results.Json(new
    {
        apiRoot = "/api/v1",
        apiKey = cfg.Config.ApiKey,
        version = VersionInfo.Version
    }));
    app.MapFallbackToFile("index.html");

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
