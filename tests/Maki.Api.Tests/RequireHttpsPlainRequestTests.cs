using System.Net;
using Maki.Api.Auth;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// With auth.requirehttps on, a request that reaches Maki as plain HTTP (a TLS proxy outside
/// auth.trustedproxies) must still be served. The antiforgery cookie cannot be forced Secure: the
/// antiforgery service throws on any non-HTTPS request when it is.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class RequireHttpsPlainRequestTests : IDisposable
{
    private const string Secret = "require-https-plain-key";
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public RequireHttpsPlainRequestTests()
    {
        CookieSession.EnsureWebRoot();
        _configDir = Path.Combine(Path.GetTempPath(), "maki-requirehttps-" + Guid.NewGuid().ToString("N"));
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
    public async Task An_authorized_get_over_plain_http_still_answers_when_https_is_required()
    {
        using (var seed = new WebApplicationFactory<Program>())
        using (var scope = seed.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.AppConfig.Add(new AppConfigEntry { Key = SettingKeys.AuthRequireHttps, Value = "true" });
            var admin = new MakiUser
            {
                UserName = "plain-admin", NormalizedUserName = "PLAIN-ADMIN",
                Permissions = MakiPermission.Admin, AllRootFolders = true
            };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            db.UserApiKeys.Add(new UserApiKey
            {
                UserId = admin.Id, Name = "plain", KeyHash = ApiKeyCrypto.Hash(Secret), Prefix = "plain",
                Scope = UserApiKeyScope.Full, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, Secret);

        var response = await client.GetAsync("/api/v1/system/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
