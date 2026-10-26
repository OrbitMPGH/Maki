using System.Net;
using System.Net.Http.Json;
using Maki.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maki.Api.Tests;

/// <summary>Ten sign-in attempts a minute per address, with no timer behind it.</summary>
public class LoginRateLimiterTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly Clock _time = new();
    private readonly LoginRateLimiter _limiter;

    public LoginRateLimiterTests() => _limiter = new LoginRateLimiter(_time);

    [Fact]
    public void The_eleventh_attempt_in_a_minute_is_refused_until_the_window_ends()
    {
        for (var i = 0; i < LoginRateLimiter.PermitLimit; i++)
        {
            Assert.True(_limiter.TryAcquire("10.0.0.1", out _));
        }

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(_limiter.TryAcquire("10.0.0.1", out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(40), retryAfter);

        _time.Advance(TimeSpan.FromSeconds(40));
        Assert.True(_limiter.TryAcquire("10.0.0.1", out _));
    }

    [Fact]
    public void One_address_spending_its_budget_leaves_another_untouched()
    {
        for (var i = 0; i < LoginRateLimiter.PermitLimit; i++)
        {
            _limiter.TryAcquire("10.0.0.1", out _);
        }

        Assert.False(_limiter.TryAcquire("10.0.0.1", out _));
        Assert.True(_limiter.TryAcquire("10.0.0.2", out _));
    }

    [Fact]
    public void Refused_attempts_do_not_extend_the_window()
    {
        for (var i = 0; i < LoginRateLimiter.PermitLimit + 5; i++)
        {
            _limiter.TryAcquire("10.0.0.1", out _);
        }

        _time.Advance(LoginRateLimiter.Window);
        Assert.True(_limiter.TryAcquire("10.0.0.1", out _));
    }

    [Fact]
    public void Addresses_whose_window_ended_are_swept_by_a_later_attempt()
    {
        for (var i = 0; i < 50; i++)
        {
            _limiter.TryAcquire($"10.0.1.{i}", out _);
        }

        Assert.Equal(50, _limiter.TrackedAddresses);

        _time.Advance(LoginRateLimiter.Window);
        _limiter.TryAcquire("10.0.0.9", out _);

        Assert.Equal(1, _limiter.TrackedAddresses);
    }
}

[Collection(ConfigDirCollection.Name)]
public sealed class LoginRateLimitPipelineTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public LoginRateLimitPipelineTests()
    {
        CookieSession.EnsureWebRoot();
        _configDir = Path.Combine(Path.GetTempPath(), "maki-loginlimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Sign_in_answers_429_after_ten_attempts_from_one_address()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var i = 0; i < LoginRateLimiter.PermitLimit; i++)
        {
            using var attempt = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "nobody", password = "wrong" });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, attempt.StatusCode);
        }

        using var refused = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "nobody", password = "wrong" });

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
    }
}
