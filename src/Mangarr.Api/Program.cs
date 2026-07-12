using Mangarr.Api.Configuration;
using Mangarr.Api.Hubs;
using Mangarr.Api.Middleware;
using Mangarr.Api.Services;
using Mangarr.Core.Download;
using Mangarr.Core.Http;
using Mangarr.Core.Metadata;
using Mangarr.Core.Sources;
using Mangarr.Data;
using Mangarr.Metadata.Embedding;
using Mangarr.Metadata.MangaBaka;
using Mangarr.Core.Configuration;
using Mangarr.Sources.MangaDex;
using Mangarr.Sources.MangaFire;
using Mangarr.Sources.MangaPill;
using Mangarr.Sources.WeebCentral;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Serilog;

var paths = new AppPaths();
var configFile = new ConfigFileProvider(paths);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<Serilog.Events.LogEventLevel>(configFile.Config.LogLevel, true, out var level)
        ? level
        : Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(paths.LogDir, "mangarr-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();
    builder.WebHost.UseUrls($"http://*:{configFile.Config.Port}");

    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(configFile);

    builder.Services.AddDbContext<MangarrDbContext>(options =>
        options.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared"));

    // MangaBaka: uncached requests are limited to 30/min (search) and 120/min (lookup).
    // Replenish smoothly (1 token / 2 s = 30/min) instead of in per-minute chunks, and
    // keep the client timeout well above the worst queue wait — library scans fire one
    // search per folder and the queue delay counts toward the HttpClient timeout.
    var mangaBakaLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(2), burst: 10);
    builder.Services.AddHttpClient(MangaBakaProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.mangabaka.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/Mangarr)");
            client.Timeout = TimeSpan.FromMinutes(3);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaBakaLimiter));

    builder.Services.AddHttpClient("covers", client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/OrbitMPGH/Mangarr)");
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    // Bulk dump downloads (~350 MB nightly snapshot) bypass the rate limiter — a single
    // long-running request, and the timeout must cover the full transfer on slow links.
    builder.Services.AddHttpClient(MangaBakaDumpService.HttpClientName, client =>
    {
        client.BaseAddress = new Uri("https://api.mangabaka.org/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/Mangarr)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });

    builder.Services.AddSingleton(new MangaBakaDumpOptions(paths.MangaBakaDbPath, paths.CacheDir));
    builder.Services.AddSingleton<MangaBakaDumpService>();
    builder.Services.AddSingleton<MangaBakaLocalStore>();
    builder.Services.AddSingleton<IMetadataProvider, MangaBakaProvider>();
    builder.Services.AddSingleton<CoverService>();
    builder.Services.AddSingleton<RecommendationService>();

    // Semantic recommendations: a local ONNX embedding model (~130 MB, downloaded on first
    // use) turns each series' description into a vector so Discover can match on "feel", not
    // just shared genre labels. The one-time index pass runs as a background job.
    builder.Services.AddHttpClient(EmbeddingModelStore.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/OrbitMPGH/Mangarr)");
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton(new EmbeddingOptions(paths.ModelsDir, paths.EmbeddingsDbPath, paths.CacheDir));
    builder.Services.AddSingleton<EmbeddingModelStore>();
    builder.Services.AddSingleton<TextEmbedder>();
    builder.Services.AddSingleton<EmbeddingStore>();
    builder.Services.AddSingleton<SeriesEmbeddingIndexer>();
    builder.Services.AddSingleton<SemanticRecommender>();

    // MangaDex API: global limit is ~5 req/s per IP. Page image hosts
    // (at-home CDN nodes) are separate and get their own client below.
    var mangaDexLimiter = RateLimitingHandler.TokenBucket(4, TimeSpan.FromSeconds(1), burst: 4);
    builder.Services.AddHttpClient(MangaDexSource.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.mangadex.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/Mangarr)");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaDexLimiter));

    builder.Services.AddHttpClient(PageDownloader.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/Mangarr)");
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
            .AddHttpMessageHandler(() => new RateLimitingHandler(limiter));
    }

    var challengeLimiter = RateLimitingHandler.TokenBucket(1, TimeSpan.FromSeconds(1), burst: 2);
    builder.Services.AddHttpClient(ChallengeAwareFetcher.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(browserUa);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(challengeLimiter));

    builder.Services.AddHttpClient(FlareSolverrClient.HttpClientName, client =>
        client.Timeout = TimeSpan.FromSeconds(90)); // FS solves can take a while

    builder.Services.AddSingleton<SettingsService>();
    builder.Services.AddSingleton<IAppSettings>(sp => sp.GetRequiredService<SettingsService>());
    builder.Services.AddSingleton<FlareSolverrClient>();
    builder.Services.AddSingleton<ChallengeAwareFetcher>();

    builder.Services.AddSingleton<ISource, MangaDexSource>();
    builder.Services.AddSingleton<ISource, MangaPillSource>();
    builder.Services.AddSingleton<ISource, WeebCentralSource>();
    builder.Services.AddSingleton<ISource, MangaFireSource>();
    builder.Services.AddSingleton<SourceRegistry>();
    builder.Services.AddSingleton<PageDownloader>();
    builder.Services.AddSingleton<EventBroadcaster>();
    builder.Services.AddSingleton<DownloadQueueService>();
    builder.Services.AddScoped<ChapterSyncService>();
    builder.Services.AddScoped<SourceMatchService>();
    builder.Services.AddScoped<ChapterDownloadProcessor>();
    builder.Services.AddScoped<LibraryImportService>();
    builder.Services.AddScoped<CbzLinkService>();
    builder.Services.AddScoped<SeriesMetadataRefreshService>();
    builder.Services.AddScoped<ReleaseService>();

    builder.Services.AddHttpClient(Mangarr.Core.Indexers.ProwlarrClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(100)); // aggregated searches fan out to indexers
    builder.Services.AddSingleton<Mangarr.Core.Indexers.ProwlarrClient>();
    builder.Services.AddSingleton<Mangarr.Core.Download.QBittorrentClient>();
    builder.Services.AddHostedService<DownloadWorkerHostedService>();

    builder.Services.AddHttpClient(Mangarr.Core.Kavita.KavitaClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton<Mangarr.Core.Kavita.KavitaClient>();
    builder.Services.AddSingleton<KavitaScanService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<KavitaScanService>());

    // Scrobbling: Kavita reading progress → AniList / MyAnimeList / MangaBaka.
    // Tracker endpoints are env-overridable so E2E tests can point at mocks.
    builder.Services.AddHttpClient(Mangarr.Core.Scrobbling.AniListTracker.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/Mangarr)");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddSingleton(new Mangarr.Core.Scrobbling.ScrobbleTrackerOptions(
        AniListApiUrl: Environment.GetEnvironmentVariable("MANGARR_SCROBBLE_ANILIST_API") ?? "https://graphql.anilist.co",
        AniListOAuthUrl: Environment.GetEnvironmentVariable("MANGARR_SCROBBLE_ANILIST_OAUTH") ?? "https://anilist.co/api/v2/oauth",
        MalApiUrl: Environment.GetEnvironmentVariable("MANGARR_SCROBBLE_MAL_API") ?? "https://api.myanimelist.net/v2",
        MalOAuthUrl: Environment.GetEnvironmentVariable("MANGARR_SCROBBLE_MAL_OAUTH") ?? "https://myanimelist.net/v1/oauth2",
        MangaBakaApiUrl: Environment.GetEnvironmentVariable("MANGARR_SCROBBLE_MANGABAKA_API") ?? "https://api.mangabaka.org"));
    builder.Services.AddSingleton<Mangarr.Core.Scrobbling.IScrobbleTokenStore, ScrobbleTokenStore>();
    builder.Services.AddSingleton<Mangarr.Core.Scrobbling.AniListTracker>();
    builder.Services.AddSingleton<Mangarr.Core.Scrobbling.MalTracker>();
    builder.Services.AddSingleton<Mangarr.Core.Scrobbling.MangaBakaTracker>();
    builder.Services.AddSingleton<ScrobbleService>();

    builder.Services.AddControllers().AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSignalR();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddQuartz(q =>
    {
        q.ScheduleJob<Mangarr.Api.Jobs.RefreshMonitoredSeriesJob>(t => t
            .WithIdentity("refresh-monitored")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(5))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(30).RepeatForever()));

        q.ScheduleJob<Mangarr.Api.Jobs.MetadataRefreshJob>(t => t
            .WithIdentity("metadata-refresh")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(15))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        q.ScheduleJob<Mangarr.Api.Jobs.HousekeepingJob>(t => t
            .WithIdentity("housekeeping")
            .StartAt(DateTimeOffset.UtcNow.AddHours(1))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        q.ScheduleJob<Mangarr.Api.Jobs.CompletedDownloadJob>(t => t
            .WithIdentity("completed-downloads")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(1))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

        // Every-minute tick; ScrobbleService decides whether the configured interval
        // has elapsed, so interval changes apply without a restart. Stable key so the
        // sync-now endpoint can trigger it with force=true.
        q.AddJob<Mangarr.Api.Jobs.ScrobbleJob>(j => j
            .WithIdentity(Mangarr.Api.Jobs.ScrobbleJob.Key));
        q.AddTrigger(t => t
            .ForJob(Mangarr.Api.Jobs.ScrobbleJob.Key)
            .WithIdentity("scrobble-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(3))
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

        // Stable job key so the settings endpoint can trigger a refresh on demand.
        q.AddJob<Mangarr.Api.Jobs.MangaBakaDumpRefreshJob>(j => j
            .WithIdentity(Mangarr.Api.Jobs.MangaBakaDumpRefreshJob.Key));
        q.AddTrigger(t => t
            .ForJob(Mangarr.Api.Jobs.MangaBakaDumpRefreshJob.Key)
            .WithIdentity("mangabaka-dump-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(2))
            .WithSimpleSchedule(s => s.WithIntervalInHours(6).RepeatForever()));

        // Embedding index for semantic recommendations. Starts a few minutes after boot (giving
        // the dump time to land on first run) and refreshes daily; skips unchanged series so
        // repeat runs are cheap. Stable key so it can be triggered on demand.
        q.AddJob<Mangarr.Api.Jobs.EmbeddingIndexJob>(j => j
            .WithIdentity(Mangarr.Api.Jobs.EmbeddingIndexJob.Key));
        q.AddTrigger(t => t
            .ForJob(Mangarr.Api.Jobs.EmbeddingIndexJob.Key)
            .WithIdentity("embedding-index-trigger")
            .StartAt(DateTimeOffset.UtcNow.AddMinutes(4))
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));
    });
    builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

    var app = builder.Build();

    // Apply migrations + enable WAL on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
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
        version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
    }));
    app.MapFallbackToFile("index.html");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Mangarr terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
