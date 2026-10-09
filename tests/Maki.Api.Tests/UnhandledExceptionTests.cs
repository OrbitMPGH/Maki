using System.Net;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maki.Api.Tests;

[ApiController]
[AllowAnonymous]
[Route("test-unhandled")]
public sealed class UnhandledExceptionProbeController : ControllerBase
{
    public static TaskCompletionSource Started = new();

    [HttpGet("ok")]
    public IActionResult Fine() => Ok();

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("probe-marker-7f3a");

    [HttpPost("reject")]
    public IActionResult Reject() =>
        throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

    [HttpGet("wait")]
    public async Task<IActionResult> Wait(CancellationToken ct)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return Ok();
    }
}

/// <summary>
/// A request that throws used to log its stack trace twice, once from request logging and once from
/// Kestrel. The handler answers it instead, so there is one entry and the usual error body.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class UnhandledExceptionTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public UnhandledExceptionTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-unhandled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        UnhandledExceptionProbeController.Started = new TaskCompletionSource();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private WebApplicationFactory<Program> Host() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddControllers().AddApplicationPart(typeof(UnhandledExceptionProbeController).Assembly)));

    private string[] LogLines() =>
        Directory.GetFiles(Path.Combine(_configDir, "logs"), "*.log")
            .SelectMany(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n');
            })
            .ToArray();

    [Fact]
    public async Task A_throwing_endpoint_answers_500_with_the_keyed_body_and_logs_the_exception_once()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test-unhandled/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error.server.unexpected", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "Something went wrong on the server. Details are in the server log.",
            body.RootElement.GetProperty("error").GetString());
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));

        var lines = LogLines();
        Assert.Single(lines, l => l.Contains("Unhandled exception in GET /test-unhandled/throw"));
        Assert.Single(lines, l => l.Contains("probe-marker-7f3a"));
    }

    [Fact]
    public async Task A_client_error_from_kestrel_keeps_its_status_and_logs_no_error()
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        using var content = new StringContent(new string('a', 200_000));

        var response = await client.PostAsync("/test-unhandled/reject", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        var lines = LogLines();
        Assert.DoesNotContain(lines, l => l.Contains("[ERR]") && l.Contains("Request body too large"));
        Assert.DoesNotContain(lines, l => l.Contains("BadHttpRequestException"));
    }

    [Fact]
    public async Task A_minimal_api_endpoint_is_covered_too()
    {
        var armed = false;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Scoped<MakiDbContext>(sp => armed
                ? throw new InvalidOperationException("probe-marker-minimal")
                : (MakiDbContext)ActivatorUtilities.CreateInstance(sp, typeof(MakiDbContext))))));
        using var client = factory.CreateClient();
        armed = true;

        var response = await client.GetAsync("/initialize.json");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error.server.unexpected", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("error").GetString()));
        Assert.Single(LogLines(), l => l.Contains("probe-marker-minimal"));
    }

    [Fact]
    public async Task A_500_behind_require_https_still_carries_strict_transport_security()
    {
        using (var seed = new WebApplicationFactory<Program>())
        using (var scope = seed.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.AppConfig.Add(new Maki.Core.Entities.AppConfigEntry { Key = SettingKeys.AuthRequireHttps, Value = "true" });
            await db.SaveChangesAsync();
        }

        using var factory = Host();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://maki.example"),
        });

        var ok = await client.GetAsync("/test-unhandled/ok");
        Assert.True(ok.Headers.Contains("Strict-Transport-Security"), "ok");
        var response = await client.GetAsync("/test-unhandled/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task A_cancelled_request_is_not_logged_as_an_error()
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();

        var pending = client.GetAsync("/test-unhandled/wait", cts.Token);
        await UnhandledExceptionProbeController.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Task.Delay(300);

        Assert.DoesNotContain(LogLines(), l => l.Contains("Unhandled exception"));
        Assert.DoesNotContain(LogLines(), l => l.Contains("[ERR]") && l.Contains("OperationCanceledException"));
    }
}
