using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

[ApiController]
[AllowAnonymous]
[Route("test-unhandled")]
public sealed class UnhandledExceptionProbeController : ControllerBase
{
    public static TaskCompletionSource Started = new();

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("probe-marker-7f3a");

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
