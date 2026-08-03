using Maki.Api.Auth;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Maki.Api.Tests;

/// <summary>
/// The authorization decisions themselves — the permission model, the CSRF filter, the last-admin
/// guard and the credential hashing. These are the rules a mistake in would silently widen access, so
/// each is exercised directly rather than through a controller.
/// </summary>
public class AuthorizationTests
{
    // ---- the permission model ----

    [Fact]
    public void AdminSatisfiesEveryPermission()
    {
        // The reason nothing may test permissions with a bare HasFlag: an admin holds only the Admin
        // bit, so a direct flag check against AddSeries would refuse the one account that certainly
        // may add series.
        foreach (var permission in Enum.GetValues<MakiPermission>())
        {
            Assert.True(MakiPermission.Admin.Grants(permission), $"Admin should grant {permission}");
        }
    }

    [Fact]
    public void NoneGrantsNothingButNone()
    {
        Assert.False(MakiPermission.None.Grants(MakiPermission.AddSeries));
        Assert.False(MakiPermission.None.Grants(MakiPermission.Admin));
    }

    [Fact]
    public void APermissionDoesNotImplyAnyOther()
    {
        var held = MakiPermission.UseOpds;

        Assert.True(held.Grants(MakiPermission.UseOpds));
        Assert.False(held.Grants(MakiPermission.AddSeries));
        Assert.False(held.Grants(MakiPermission.Admin));
        // Especially not the reverse implication: holding a permission must never confer admin.
        Assert.False(held.Grants(MakiPermission.UseOpds | MakiPermission.Admin));
    }

    [Fact]
    public void AllNonAdminIsEveryPermissionExceptAdmin()
    {
        Assert.False(MakiPermissions.AllNonAdmin.HasFlag(MakiPermission.Admin));
        Assert.True(MakiPermissions.AllNonAdmin.Grants(MakiPermission.ImportLibrary));
        // Grants() would answer true for Admin if the Admin bit had leaked in, so test the bit itself.
        Assert.False((MakiPermissions.AllNonAdmin & MakiPermission.Admin) != 0);
    }

    [Fact]
    public void ANewUsersDefaultsCannotWriteToTheSharedLibrary()
    {
        var defaults = MakiPermissions.DefaultForNewUser;

        Assert.False(defaults.Grants(MakiPermission.Admin));
        Assert.False(defaults.Grants(MakiPermission.AddSeries));
        Assert.False(defaults.Grants(MakiPermission.DeleteSeries));
        Assert.False(defaults.Grants(MakiPermission.DownloadChapters));
        Assert.False(defaults.Grants(MakiPermission.ManageTags));
    }

    [Fact]
    public void PermissionBitPositionsAreStable()
    {
        // These values are persisted in AspNetUsers.Permissions. Renumbering a member silently
        // re-grants a different permission to every existing account.
        Assert.Equal(1, (int)MakiPermission.Admin);
        Assert.Equal(2, (int)MakiPermission.AddSeries);
        Assert.Equal(4, (int)MakiPermission.DeleteSeries);
        Assert.Equal(8, (int)MakiPermission.DownloadChapters);
        Assert.Equal(16, (int)MakiPermission.ManageDownloadQueue);
        Assert.Equal(32, (int)MakiPermission.ManageSources);
        Assert.Equal(64, (int)MakiPermission.EditMetadata);
        Assert.Equal(128, (int)MakiPermission.ManageTags);
        Assert.Equal(256, (int)MakiPermission.ChangeContentRating);
        Assert.Equal(512, (int)MakiPermission.UseTrackers);
        Assert.Equal(1024, (int)MakiPermission.UseOpds);
        Assert.Equal(2048, (int)MakiPermission.ImportLibrary);
    }

    // ---- policies ----

    [Fact]
    public void EveryPermissionHasAPolicy()
    {
        var options = new AuthorizationOptions();
        options.AddMakiPolicies();

        foreach (var permission in Enum.GetValues<MakiPermission>())
        {
            if (permission == MakiPermission.None) continue;
            Assert.NotNull(options.GetPolicy(permission.ToString()));
        }
    }

    [Fact]
    public void TheFallbackPolicyRequiresAuthentication()
    {
        var options = new AuthorizationOptions();
        options.AddMakiPolicies();

        // This is what makes a newly added controller protected by default rather than wide open.
        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(
            options.FallbackPolicy!.Requirements,
            r => r is DenyAnonymousAuthorizationRequirement);
    }

    // ---- the permission handler ----

    private sealed class FakeCurrentUser(bool authenticated, MakiPermission permissions) : ICurrentUser
    {
        public bool IsAuthenticated { get; } = authenticated;
        public int UserId => 1;
        public string UserName => "test";
        public MakiPermission Permissions { get; } = permissions;
        public bool AllRootFolders => true;
        public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
        public string MaxContentRating => "safe";
    }

    private static async Task<bool> Authorizes(ICurrentUser user, MakiPermission required)
    {
        var requirement = new PermissionRequirement(required);
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(), null);
        await new PermissionAuthorizationHandler(user).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task TheHandlerAllowsAHeldPermission()
    {
        Assert.True(await Authorizes(
            new FakeCurrentUser(true, MakiPermission.AddSeries), MakiPermission.AddSeries));
    }

    [Fact]
    public async Task TheHandlerAllowsAnAdminAnything()
    {
        Assert.True(await Authorizes(
            new FakeCurrentUser(true, MakiPermission.Admin), MakiPermission.DeleteSeries));
    }

    [Fact]
    public async Task TheHandlerRefusesAMissingPermission()
    {
        Assert.False(await Authorizes(
            new FakeCurrentUser(true, MakiPermission.UseOpds), MakiPermission.DeleteSeries));
    }

    [Fact]
    public async Task TheHandlerRefusesAnUnauthenticatedCaller()
    {
        // Belt and braces behind the fallback policy: even a principal carrying every permission is
        // refused if the request was never authenticated.
        Assert.False(await Authorizes(
            new FakeCurrentUser(false, MakiPermission.Admin), MakiPermission.AddSeries));
    }

    // ---- API key hashing ----

    [Fact]
    public void GeneratedKeysAre256BitsOfHex()
    {
        var key = ApiKeyCrypto.Generate();

        Assert.Equal(64, key.Length);
        Assert.True(key.All(Uri.IsHexDigit));
        Assert.NotEqual(key, ApiKeyCrypto.Generate());
    }

    [Fact]
    public void HashingIsStableAndDoesNotRevealTheKey()
    {
        var key = ApiKeyCrypto.Generate();
        var hash = ApiKeyCrypto.Hash(key);

        // Stable, because it is the lookup index.
        Assert.Equal(hash, ApiKeyCrypto.Hash(key));
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(key, hash, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(hash, ApiKeyCrypto.Hash(ApiKeyCrypto.Generate()));
    }

    [Fact]
    public void HashingIsCaseSensitive()
    {
        var key = ApiKeyCrypto.Generate();
        Assert.NotEqual(ApiKeyCrypto.Hash(key), ApiKeyCrypto.Hash(key.ToUpperInvariant()));
    }

    [Fact]
    public void ThePrefixIsShortEnoughToBeUseless()
    {
        var key = ApiKeyCrypto.Generate();
        var prefix = ApiKeyCrypto.Prefix(key);

        Assert.Equal(ApiKeyCrypto.PrefixLength, prefix.Length);
        Assert.StartsWith(prefix, key, StringComparison.Ordinal);
    }

    // ---- permission names sent to the client ----

    [Fact]
    public void AdminExpandsToEveryPermissionNameForTheClient()
    {
        var names = UserDtoMapper.Names(MakiPermission.Admin);

        // Expanded server-side so the client never has to reimplement "Admin implies everything";
        // getting that wrong in the UI greys out controls for the one account that may use them.
        Assert.Contains(nameof(MakiPermission.Admin), names);
        Assert.Contains(nameof(MakiPermission.DeleteSeries), names);
        Assert.Equal(Enum.GetValues<MakiPermission>().Length - 1, names.Count);
    }

    [Fact]
    public void NoneExpandsToNothing()
    {
        Assert.Empty(UserDtoMapper.Names(MakiPermission.None));
    }

    // ---- the last-admin guard ----

    [Fact]
    public async Task TheOnlyAdminIsTheLastAdmin()
    {
        using var db = new TestDb();
        var adminId = db.SeedUser("admin", MakiPermission.Admin);
        db.SeedUser("reader", MakiPermission.UseOpds);

        Assert.True(await new AdminGuard(db.NewContext()).IsLastAdminAsync(adminId, default));
    }

    [Fact]
    public async Task ASecondAdminMakesTheFirstDemotable()
    {
        using var db = new TestDb();
        var firstId = db.SeedUser("admin", MakiPermission.Admin);
        db.SeedUser("admin2", MakiPermission.Admin);

        Assert.False(await new AdminGuard(db.NewContext()).IsLastAdminAsync(firstId, default));
    }

    [Fact]
    public async Task ADisabledAdminIsNotAWayBackIn()
    {
        using var db = new TestDb();
        var adminId = db.SeedUser("admin", MakiPermission.Admin);
        db.SeedUser("spare", MakiPermission.Admin, configure: u => u.Disabled = true);

        // Counting an account that cannot sign in would let the real admin demote themselves and lock
        // the instance out of its own settings permanently.
        Assert.True(await new AdminGuard(db.NewContext()).IsLastAdminAsync(adminId, default));
    }

    [Fact]
    public async Task AnUnclaimedAdminIsNotAWayBackIn()
    {
        using var db = new TestDb();
        var adminId = db.SeedUser("admin", MakiPermission.Admin);
        db.SeedUser("placeholder", MakiPermission.Admin, configure: u => u.PendingSetup = true);

        Assert.True(await new AdminGuard(db.NewContext()).IsLastAdminAsync(adminId, default));
    }

    // ---- CSRF ----

    private static async Task<IActionResult?> RunAntiforgeryFilter(
        string method, string? authenticationType, bool tokenValid = false)
    {
        var services = new ServiceCollection();
        services.AddAntiforgery();
        services.AddLogging();
        services.AddDataProtection();
        var provider = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = method;
        http.User = authenticationType is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([], authenticationType));

        var context = new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor(), new ModelStateDictionary()),
            []);

        var antiforgery = tokenValid
            ? new AlwaysValidAntiforgery()
            : provider.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();

        await new AntiforgeryCookieFilter(antiforgery).OnAuthorizationAsync(context);
        return context.Result;
    }

    /// <summary>Stands in for a request that carried a valid token, without minting a real one.</summary>
    private sealed class AlwaysValidAntiforgery : Microsoft.AspNetCore.Antiforgery.IAntiforgery
    {
        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetAndStoreTokens(HttpContext c) =>
            new("r", "c", "f", "h");
        public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetTokens(HttpContext c) =>
            new("r", "c", "f", "h");
        public Task<bool> IsRequestValidAsync(HttpContext c) => Task.FromResult(true);
        public void SetCookieTokenAndHeader(HttpContext c) { }
        public Task ValidateRequestAsync(HttpContext c) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SafeMethodsNeedNoAntiforgeryToken(string method)
    {
        Assert.Null(await RunAntiforgeryFilter(method, IdentityConstants.ApplicationScheme));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task CookieAuthenticatedMutationsAreRejectedWithoutAToken(string method)
    {
        var result = await RunAntiforgeryFilter(method, IdentityConstants.ApplicationScheme);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task CookieAuthenticatedMutationsPassWithAValidToken()
    {
        Assert.Null(await RunAntiforgeryFilter("POST", IdentityConstants.ApplicationScheme, tokenValid: true));
    }

    [Fact]
    public async Task ApiKeyAuthenticatedMutationsNeedNoAntiforgeryToken()
    {
        // A header credential is never sent ambiently by a browser, so there is nothing to forge —
        // and demanding a token here would break every script and third-party client for no gain.
        Assert.Null(await RunAntiforgeryFilter("POST", AuthSchemes.ApiKey));
    }

    [Fact]
    public async Task AnonymousMutationsNeedNoAntiforgeryToken()
    {
        // The OAuth callback and first-run setup are anonymous; they hold no ambient credential to
        // ride on, so CSRF does not apply and the filter must not block them.
        Assert.Null(await RunAntiforgeryFilter("POST", authenticationType: null));
    }

    // ---- auth.* settings ----

    [Fact]
    public async Task SecuritySettingsFallBackToSafeDefaults()
    {
        using var db = new TestDb();
        db.SetConfig();

        var options = new AuthRuntimeOptions();
        await options.LoadAsync(db.NewContext());

        // HTTPS enforcement defaults off: the common deployment is plain HTTP on a LAN, where a
        // Secure cookie is set and then never sent back, breaking sign-in with nothing in any log.
        Assert.False(options.RequireHttps);
        // Forwarded headers off by default, so a forged X-Forwarded-For cannot dodge rate limiting.
        Assert.Empty(options.TrustedProxies);
        Assert.Equal(AuthRuntimeOptions.DefaultLockoutMaxAttempts, options.LockoutMaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(AuthRuntimeOptions.DefaultLockoutMinutes), options.LockoutDuration);
        Assert.Equal(TimeSpan.FromDays(AuthRuntimeOptions.DefaultSessionDays), options.SessionLifetime);
    }

    [Fact]
    public async Task SecuritySettingsAreReadBack()
    {
        using var db = new TestDb();
        db.SetConfig(
            (SettingKeys.AuthRequireHttps, "true"),
            (SettingKeys.AuthTrustedProxies, "10.0.0.1, 172.18.0.0/16"),
            (SettingKeys.AuthLockoutMaxAttempts, "3"),
            (SettingKeys.AuthLockoutMinutes, "60"),
            (SettingKeys.AuthSessionDays, "7"));

        var options = new AuthRuntimeOptions();
        await options.LoadAsync(db.NewContext());

        Assert.True(options.RequireHttps);
        Assert.Equal(["10.0.0.1", "172.18.0.0/16"], options.TrustedProxies);
        Assert.Equal(3, options.LockoutMaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(60), options.LockoutDuration);
        Assert.Equal(TimeSpan.FromDays(7), options.SessionLifetime);
    }

    [Fact]
    public async Task ZeroLockoutAttemptsIsHonouredAsMeaningLockoutOff()
    {
        using var db = new TestDb();
        db.SetConfig((SettingKeys.AuthLockoutMaxAttempts, "0"));

        var options = new AuthRuntimeOptions();
        await options.LoadAsync(db.NewContext());

        // Zero is a real setting, not a missing one — it must survive rather than snapping back to
        // the default, or "lockout disabled" would silently mean "locks on the first failure".
        Assert.Equal(0, options.LockoutMaxAttempts);
    }

    [Fact]
    public async Task NonsenseSecuritySettingsFallBackRatherThanThrow()
    {
        using var db = new TestDb();
        db.SetConfig(
            (SettingKeys.AuthLockoutMinutes, "not-a-number"),
            (SettingKeys.AuthSessionDays, "-5"));

        var options = new AuthRuntimeOptions();
        await options.LoadAsync(db.NewContext());

        // Startup reads these; throwing here would make the app unbootable over a typo in a setting.
        Assert.Equal(TimeSpan.FromMinutes(AuthRuntimeOptions.DefaultLockoutMinutes), options.LockoutDuration);
        Assert.Equal(TimeSpan.FromDays(AuthRuntimeOptions.DefaultSessionDays), options.SessionLifetime);
    }
}
