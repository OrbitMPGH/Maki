using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Maki.Api.Auth;
using Maki.Api.Komga;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// The Komga API's credentials through the real host. <see cref="KomgaAuthFilter"/> is what
/// authenticates every action, <see cref="ApiKeyAuthenticationHandler"/> must let a Komga client's
/// <c>X-API-Key</c> through untouched, and the tracker's PUT rides on the issued cookie alone; the
/// controller tests start past all three.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class KomgaPipelineTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public KomgaPipelineTests()
    {
        CookieSession.EnsureWebRoot();
        _configDir = Path.Combine(Path.GetTempPath(), "maki-komga-pipeline-" + Guid.NewGuid().ToString("N"));
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

    private static HttpRequestMessage Get(string path, Action<HttpRequestHeaders>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        headers?.Invoke(request.Headers);
        return request;
    }

    [Fact]
    public async Task The_opds_token_opens_the_api_and_nothing_else_does()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var seeded = await ReadingAppSeed.SeedAsync(factory, Path.Combine(_configDir, "library"),
            (SettingKeys.KomgaEnabled, "true"));
        string full;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            full = ApiKeyCrypto.Generate();
            db.UserApiKeys.Add(new UserApiKey
            {
                UserId = seeded.UserId, Name = "full", KeyHash = ApiKeyCrypto.Hash(full), Prefix = ApiKeyCrypto.Prefix(full),
                Scope = UserApiKeyScope.Full, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        const string series = "/komga/api/v1/series";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get(series))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Get(series, h => h.Add(KomgaAuthenticator.ApiKeyHeader, full)))).StatusCode);

        var keyed = await client.SendAsync(Get(series, h => h.Add(KomgaAuthenticator.ApiKeyHeader, seeded.Token)));
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
        Assert.Contains(keyed.Headers.GetValues("Set-Cookie"), c => c.StartsWith(KomgaAuthenticator.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains($"\"id\":\"{seeded.SeriesId}\"", await keyed.Content.ReadAsStringAsync());

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("anyone:" + seeded.Token));
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Get(series, h => h.Authorization = new AuthenticationHeaderValue("Basic", basic)))).StatusCode);
    }

    [Fact]
    public async Task The_issued_cookie_alone_carries_the_trackers_put_and_patch_but_never_a_post()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var seeded = await ReadingAppSeed.SeedAsync(factory, Path.Combine(_configDir, "library"),
            (SettingKeys.KomgaEnabled, "true"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(
            Get("/komga/api/v1/login/set-cookie", h => h.Add(KomgaAuthenticator.ApiKeyHeader, seeded.Token)))).StatusCode);

        // From here the client sends nothing but the cookie, as the Mihon tracker does.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/komga/api/v1/series/{seeded.SeriesId}/read-progress", null)).StatusCode);

        var patched = await client.PatchAsJsonAsync($"/komga/api/v1/books/{seeded.ChapterId}/read-progress", new { page = 1 });
        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);

        var put = await client.PutAsJsonAsync($"/komga/api/v2/series/{seeded.SeriesId}/read-progress/tachiyomi",
            new { lastBookNumberSortRead = 1 });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        Assert.True(await db.ChapterProgress.IgnoreQueryFilters()
            .AnyAsync(p => p.ChapterId == seeded.ChapterId && p.UserId == seeded.UserId && p.Completed));
    }
}
