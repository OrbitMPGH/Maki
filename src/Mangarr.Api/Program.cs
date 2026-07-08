using Mangarr.Api.Configuration;
using Mangarr.Api.Middleware;
using Mangarr.Api.Services;
using Mangarr.Core.Http;
using Mangarr.Core.Metadata;
using Mangarr.Data;
using Mangarr.Metadata.MangaBaka;
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

    // MangaBaka: uncached requests are limited to 30/min (search) and 120/min (lookup);
    // a single conservative 30/min bucket keeps us safely under both.
    var mangaBakaLimiter = RateLimitingHandler.TokenBucket(30, TimeSpan.FromMinutes(1), burst: 10);
    builder.Services.AddHttpClient(MangaBakaProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.mangabaka.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/Mangarr)");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new RateLimitingHandler(mangaBakaLimiter));

    builder.Services.AddHttpClient("covers", client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mangarr/1.0 (+https://github.com/Mangarr)");
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    builder.Services.AddSingleton<IMetadataProvider, MangaBakaProvider>();
    builder.Services.AddSingleton<CoverService>();

    builder.Services.AddControllers().AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSignalR();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddQuartz();
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
