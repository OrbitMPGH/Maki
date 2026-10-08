using System.Net;
using System.Net.Http.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// A trusted-proxy setting with no usable entry must leave forwarded headers ignored. With empty
/// known-proxy lists the middleware would trust X-Forwarded-For from every client.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class TrustedProxyStartupTests : IDisposable
{
    private const string ForgedAddress = "203.0.113.9";
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public TrustedProxyStartupTests()
    {
        CookieSession.EnsureWebRoot();
        _configDir = Path.Combine(Path.GetTempPath(), "maki-trustedproxy-" + Guid.NewGuid().ToString("N"));
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

    // The trusted entry is the positive control: it proves the forged header is honoured when the
    // caller really is a trusted proxy, so the unusable case is not passing for some other reason.
    [Theory]
    [InlineData("10.0.0.0/abc", "127.0.0.1")]
    [InlineData("127.0.0.1", ForgedAddress)]
    public async Task Forwarded_headers_are_honoured_only_from_a_usable_trusted_entry(string entry, string expected)
    {
        // The setting is read once at startup, so seed it on a first boot and observe on a second.
        using (var seed = new WebApplicationFactory<Program>())
        using (var scope = seed.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.AppConfig.Add(new AppConfigEntry { Key = SettingKeys.AuthTrustedProxies, Value = entry });
            await db.SaveChangesAsync();
        }

        using var host = new WebApplicationFactory<Program>();
        using var factory = host.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, LoopbackCallerFilter>()));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "nobody", password = "wrong password" })
        };
        request.Headers.Add("X-Forwarded-For", ForgedAddress);
        await client.SendAsync(request);

        using var check = factory.Services.CreateScope();
        var events = await check.ServiceProvider.GetRequiredService<MakiDbContext>().AuthEvents
            .AsNoTracking()
            .Where(e => e.UserName == "nobody")
            .ToListAsync();
        var recorded = Assert.Single(events);
        Assert.Equal(expected, recorded.ClientIp);
    }

    /// <summary>TestServer leaves the caller's address unset; give it the loopback a real one would have.</summary>
    private sealed class LoopbackCallerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
