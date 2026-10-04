using System.Security.Claims;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The account-protecting paths around passwords, second factors and single sign-on links: who may
/// add a login, how a recovery code is read and counted, and what an admin may undo for another user.
/// </summary>
public sealed class AuthHardeningTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly TestDb _db = new();
    private readonly StoppedClock _clock = new(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private static string CodeOf(IActionResult result)
    {
        var body = result switch
        {
            ObjectResult o => o.Value!,
            _ => throw new Xunit.Sdk.XunitException($"Expected a body, got {result.GetType().Name}")
        };
        return (string)body.GetType().GetProperty("code")!.GetValue(body)!;
    }

    private int SeedWithPassword(string name, MakiPermission permissions = MakiPermission.None) =>
        _db.SeedUser(name, permissions, configure: u => u.PasswordHash = IdentityTestKit.Hash(u, Password));

    private void LinkOidc(int userId, string subject)
    {
        using var db = _db.NewContext();
        db.UserLogins.Add(new IdentityUserLogin<int>
        {
            LoginProvider = AuthSchemes.Oidc,
            ProviderKey = $"https://auth.example.com|{subject}",
            ProviderDisplayName = subject,
            UserId = userId
        });
        db.SaveChanges();
    }

    private int OidcLoginCount(int userId)
    {
        using var db = _db.NewContext();
        return db.UserLogins.Count(l => l.UserId == userId && l.LoginProvider == AuthSchemes.Oidc);
    }

    private async Task<OidcRuntimeOptions> OidcAsync(MakiDbContext db)
    {
        _db.SetConfig(
            (SettingKeys.AuthOidcEnabled, "true"),
            (SettingKeys.AuthOidcAuthority, "https://auth.example.com"),
            (SettingKeys.AuthOidcClientId, "maki"));
        var oidc = new OidcRuntimeOptions();
        await oidc.LoadAsync(db);
        return oidc;
    }

    private AuthController Auth(
        MakiDbContext db, UserManager<MakiUser> users, SignInManager<MakiUser> signIn, OidcRuntimeOptions oidc,
        int userId, IAuthenticationService? authentication = null)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        if (authentication is not null)
        {
            services.AddSingleton(authentication);
        }

        return new AuthController(
            new TestLocalizer(), db, users, signIn, new PasswordHasher<MakiUser>(), new NoopAntiforgery(),
            new TestCurrentUser(userId), new AuthEventLogger(db, _clock), oidc, null!, _clock,
            NullLogger<AuthController>.Instance, Inbox())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                    User = IdentityTestKit.Principal(userId)
                }
            }
        };
    }

    private Maki.Api.Services.InboxService Inbox()
    {
        var scopes = _db.ScopeFactory();
        return new Maki.Api.Services.InboxService(scopes, new Maki.Api.Services.InboxAudienceResolver(scopes),
            new Maki.Api.Hubs.EventBroadcaster(new NoopHubContext(), scopes), _clock,
            NullLogger<Maki.Api.Services.InboxService>.Instance);
    }

    // ---- single sign-on linking ----

    [Fact]
    public async Task A_passwordless_sso_account_cannot_start_a_second_link()
    {
        var userId = _db.SeedUser("ada", MakiPermission.None);
        LinkOidc(userId, "sub-1");
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);
        var controller = Auth(db, users, new TestSignInManager(users), await OidcAsync(db), userId);

        var result = await controller.OidcLinkConfirm(new ConfirmPasswordRequest(null));

        // Nothing to confirm used to mean straight through: a stolen session could then attach the
        // thief's own provider account as a second way in.
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("error.auth.ssoAlreadyLinked", CodeOf(result));
        Assert.False(controller.Response.Headers.SetCookie.ToString().Contains(OidcLinkIntent.CookieName));
    }

    [Fact]
    public async Task Link_complete_refuses_a_second_oidc_login()
    {
        var userId = SeedWithPassword("ada");
        LinkOidc(userId, "sub-1");
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);
        var controller = Auth(db, users, new TestSignInManager(users), await OidcAsync(db), userId,
            new ExternalTicket("sub-2", userId));

        var result = await controller.OidcLinkComplete(default);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/settings?oidcLinkError=error.auth.ssoAlreadyLinked", redirect.Url);
        Assert.Equal(1, OidcLoginCount(userId));
    }

    [Fact]
    public async Task Link_complete_still_links_an_account_with_no_oidc_login()
    {
        var userId = SeedWithPassword("ada");
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);
        var controller = Auth(db, users, new TestSignInManager(users), await OidcAsync(db), userId,
            new ExternalTicket("sub-2", userId));

        var result = await controller.OidcLinkComplete(default);

        Assert.Equal("/settings?oidcLinked=1", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(1, OidcLoginCount(userId));
    }

    // ---- recovery codes at sign-in ----

    private async Task<(AuthController Controller, TestSignInManager SignIn, MakiDbContext Db, IReadOnlyList<string> Codes)>
        TwoFactorAsync(int userId, bool lockedOut = false)
    {
        var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);
        var user = (await users.FindByIdAsync(userId.ToString()))!;
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.ToList();
        if (lockedOut)
        {
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
        }

        var signIn = new TestSignInManager(users, user);
        return (Auth(db, users, signIn, new OidcRuntimeOptions(), userId), signIn, db, codes);
    }

    [Fact]
    public async Task A_recovery_code_typed_in_lower_case_without_the_dash_is_redeemed()
    {
        var userId = SeedWithPassword("ada");
        var (controller, signIn, db, codes) = await TwoFactorAsync(userId);
        using var _ = db;
        var typed = codes[0].Replace("-", " ").ToLowerInvariant();

        var result = await controller.TwoFactor(new TwoFactorRequest(typed, false), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal([codes[0]], signIn.RedeemedRecoveryCodes);
    }

    [Fact]
    public async Task A_wrong_recovery_code_counts_toward_lockout()
    {
        var userId = SeedWithPassword("ada");
        var (controller, _, db, _) = await TwoFactorAsync(userId);
        using var __ = db;

        var result = await controller.TwoFactor(new TwoFactorRequest("WRONG-CODE1", false), default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        using var check = _db.NewContext();
        Assert.Equal(1, check.Users.Single(u => u.Id == userId).AccessFailedCount);
    }

    [Fact]
    public async Task A_locked_out_account_cannot_redeem_even_a_valid_recovery_code()
    {
        var userId = SeedWithPassword("ada");
        var (controller, signIn, db, codes) = await TwoFactorAsync(userId, lockedOut: true);
        using var _ = db;

        var result = await controller.TwoFactor(new TwoFactorRequest(codes[0], false), default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(signIn.RedeemedRecoveryCodes);
        using var check = _db.NewContext();
        var users = IdentityTestKit.UserManager(check);
        Assert.Equal(2, await users.CountRecoveryCodesAsync((await users.FindByIdAsync(userId.ToString()))!));
    }

    // ---- account password confirmation ----

    [Fact]
    public async Task Turning_off_two_factor_on_a_locked_out_account_says_locked_out()
    {
        var userId = _db.SeedUser("ada", MakiPermission.None, configure: u =>
        {
            u.PasswordHash = IdentityTestKit.Hash(u, Password);
            u.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
        });
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);
        var controller = new AccountController(
            new TestLocalizer(), db, users, new TestSignInManager(users), new TestCurrentUser(userId),
            new AuthEventLogger(db, _clock), new OidcRuntimeOptions(), _clock,
            new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions())))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.DisableTwoFactor(new DisableTwoFactorRequest(Password), default);

        // The same answer every other password prompt gives, rather than "incorrect" for a right one.
        Assert.Equal("error.account.lockedOut", CodeOf(result));
    }

    // ---- admin user management ----

    private UsersController Users(MakiDbContext db, int adminId, OidcRuntimeOptions? oidc = null) =>
        new(new TestLocalizer(), db, IdentityTestKit.UserManager(db), new AdminGuard(db),
            new TestCurrentUser(adminId, "admin"), new AuthEventLogger(db, _clock), _clock,
            NullLogger<UsersController>.Instance, oidc ?? new OidcRuntimeOptions(), new NoopHubContext(),
            new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions())))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    [Fact]
    public async Task A_rejected_password_reset_leaves_the_earlier_edits_unapplied()
    {
        var adminId = _db.SeedUser("admin");
        var readerId = SeedWithPassword("reader");
        using var db = _db.NewContext();

        var result = await Users(db, adminId).Update(readerId, new SaveUserRequest(
            "renamed", "short", null, MakiPermission.AddSeries, null, null, null, null), default);

        // The rename used to commit the permission edit with it before the password was refused.
        Assert.IsType<BadRequestObjectResult>(result);
        using var check = _db.NewContext();
        var reader = check.Users.Single(u => u.Id == readerId);
        Assert.Equal("reader", reader.UserName);
        Assert.Equal(MakiPermission.None, reader.Permissions);
    }

    [Fact]
    public async Task An_invalid_rating_is_refused_before_anything_is_written()
    {
        var adminId = _db.SeedUser("admin");
        var readerId = SeedWithPassword("reader");
        using var db = _db.NewContext();

        var result = await Users(db, adminId).Update(readerId, new SaveUserRequest(
            "renamed", null, null, MakiPermission.AddSeries, "nonsense", null, null, null), default);

        Assert.Equal("error.users.invalidContentRating", CodeOf(result));
        using var check = _db.NewContext();
        Assert.Equal("reader", check.Users.Single(u => u.Id == readerId).UserName);
    }

    [Fact]
    public async Task An_admin_cannot_reset_their_own_two_factor_here()
    {
        var adminId = _db.SeedUser("admin");
        using var db = _db.NewContext();

        var result = await Users(db, adminId).ResetTwoFactor(adminId, default);

        Assert.Equal("error.users.cannotResetOwnTwoFactor", CodeOf(result));
    }

    [Fact]
    public async Task Resetting_another_users_two_factor_turns_it_off_and_replaces_the_secret()
    {
        var adminId = _db.SeedUser("admin");
        var readerId = SeedWithPassword("reader");
        string? keyBefore;
        using (var seed = _db.NewContext())
        {
            var users = IdentityTestKit.UserManager(seed);
            var reader = (await users.FindByIdAsync(readerId.ToString()))!;
            await users.ResetAuthenticatorKeyAsync(reader);
            await users.SetTwoFactorEnabledAsync(reader, true);
            keyBefore = await users.GetAuthenticatorKeyAsync(reader);
        }

        using var db = _db.NewContext();
        var result = await Users(db, adminId).ResetTwoFactor(readerId, default);

        Assert.IsType<NoContentResult>(result);
        using var check = _db.NewContext();
        var checkUsers = IdentityTestKit.UserManager(check);
        var after = (await checkUsers.FindByIdAsync(readerId.ToString()))!;
        Assert.False(after.TwoFactorEnabled);
        Assert.NotEqual(keyBefore, await checkUsers.GetAuthenticatorKeyAsync(after));
        Assert.Contains(check.AuthEvents, e => e.Type == AuthEventType.TwoFactorDisabled);
    }

    [Fact]
    public async Task Unlinking_sso_is_refused_when_it_is_the_accounts_only_way_in()
    {
        var adminId = _db.SeedUser("admin");
        var readerId = _db.SeedUser("reader", MakiPermission.None);
        LinkOidc(readerId, "sub-1");
        using var db = _db.NewContext();

        var result = await Users(db, adminId).UnlinkOidc(readerId, default);

        Assert.Equal("error.users.onlySignInMethod", CodeOf(result));
        Assert.Equal(1, OidcLoginCount(readerId));
    }

    [Fact]
    public async Task Unlinking_sso_removes_it_from_an_account_that_still_has_a_password()
    {
        var adminId = _db.SeedUser("admin");
        var readerId = SeedWithPassword("reader");
        LinkOidc(readerId, "sub-1");
        using var db = _db.NewContext();

        var result = await Users(db, adminId).UnlinkOidc(readerId, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, OidcLoginCount(readerId));
    }

    // ---- fixture plumbing ----

    /// <summary>The ticket the OIDC handler leaves in the external cookie after a link challenge.</summary>
    private sealed class ExternalTicket(string subject, int linkUserId) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            var properties = new AuthenticationProperties();
            properties.Items[OidcLinkIntent.PropertyKey] = linkUserId.ToString();
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "oidc"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, properties, IdentityConstants.ExternalScheme)));
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignInAsync(
            HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }

    private sealed class NoopAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => new(null, null, "f", null);
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => new(null, null, "f", null);
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);
        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
    }
}
