using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maki.Api.Auth;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// Admin user management and the token-minting settings refuse an API key, even an admin's Full one,
/// through the real pipeline: a leaked key must not be able to create accounts, reset passwords or
/// repoint single sign-on, each of which outlives revoking it.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class CookieSessionOnlyTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public CookieSessionOnlyTests()
    {
        CookieSession.EnsureWebRoot();
        _configDir = Path.Combine(Path.GetTempPath(), "maki-cookieonly-" + Guid.NewGuid().ToString("N"));
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
    public async Task An_admin_api_key_is_refused_on_session_only_endpoints()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var admin = new MakiUser
            {
                UserName = "key-admin", NormalizedUserName = "KEY-ADMIN",
                Permissions = MakiPermission.Admin, AllRootFolders = true
            };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            const string secret = "cookie-only-admin-key";
            db.UserApiKeys.Add(new UserApiKey
            {
                UserId = admin.Id, Name = "admin script", KeyHash = ApiKeyCrypto.Hash(secret), Prefix = "cookie",
                Scope = UserApiKeyScope.Full, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, secret);
        }

        // The key itself works: an ordinary admin read answers.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/settings/security")).StatusCode);

        var refused = new[]
        {
            await client.GetAsync("/api/v1/users"),
            await client.PostAsJsonAsync("/api/v1/users", new { username = "minted", password = "a long password" }),
            await client.PutAsJsonAsync("/api/v1/settings/oidc", new { enabled = false }),
            await client.PostAsync("/api/v1/settings/opds/token", null),
        };

        foreach (var response in refused)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("error.account.sessionRequired", body.RootElement.GetProperty("code").GetString());
        }
    }
}

/// <summary>Signs a <see cref="WebApplicationFactory{TEntryPoint}"/> client in the way the SPA does.</summary>
internal static class CookieSession
{
    /// <summary>wwwroot is gitignored and the host refuses to start without it.</summary>
    public static void EnsureWebRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var api = Path.Combine(dir.FullName, "src", "Maki.Api");
            if (File.Exists(Path.Combine(api, "Maki.Api.csproj")))
            {
                Directory.CreateDirectory(Path.Combine(api, "wwwroot"));
                return;
            }
        }
    }

    /// <summary>
    /// Creates the account with a password, posts it to <c>auth/login</c> so the client's cookie
    /// container holds the session, and echoes the antiforgery token the login handed back.
    /// </summary>
    public static async Task<int> SignInAsync(
        WebApplicationFactory<Program> factory, HttpClient client, MakiUser user, string password)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<UserManager<MakiUser>>()
                .CreateAsync(user, password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user.UserName, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var token = login.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';', 2)[0])
            .Last(c => c.StartsWith(AntiforgeryTokenMiddleware.CookieName + "=", StringComparison.Ordinal))
            [(AntiforgeryTokenMiddleware.CookieName.Length + 1)..];
        client.DefaultRequestHeaders.Add(AntiforgeryCookieFilter.HeaderName, Uri.UnescapeDataString(token));
        return user.Id;
    }
}
