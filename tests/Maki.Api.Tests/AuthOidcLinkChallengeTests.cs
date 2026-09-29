using System.Security.Claims;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Maki.Api.Tests;

/// <summary>
/// <c>AuthController.OidcLink</c> used to <c>return Challenge(...)</c>, whose
/// <c>ChallengeResult.ExecuteResultAsync</c> runs the OIDC handler outside this method's own stack,
/// a provider rejecting the request synchronously (PAR refusing the client credentials, say) surfaced
/// as an unhandled 500 rather than the redirect <c>OidcChallenge</c> already gives the sign-in page
/// for the same failure. This proves the settings-page counterpart now behaves the same way.
/// </summary>
public class AuthOidcLinkChallengeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Stands in for the real OIDC handler and fails the way a provider rejection does.</summary>
    private sealed class ThrowingAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            throw new NotImplementedException();

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new OpenIdConnectProtocolException("provider rejected the request");

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotImplementedException();

        public Task SignInAsync(
            HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotImplementedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotImplementedException();
    }

    private async Task<AuthController> ControllerAsync(
        int userId, IAuthenticationService authService, bool confirmed = true)
    {
        _db.SetConfig(
            (SettingKeys.AuthOidcEnabled, "true"),
            (SettingKeys.AuthOidcAuthority, "https://auth.example.com"),
            (SettingKeys.AuthOidcClientId, "maki"));
        var db = _db.NewContext(userId);
        var oidc = new OidcRuntimeOptions();
        await oidc.LoadAsync(db);

        var controller = new AuthController(
            new TestLocalizer(), db, null!, null!, null!, null!, new TestCurrentUser(userId),
            null!, oidc, null!, new StoppedClock(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<AuthController>.Instance, null!);

        var services = new ServiceCollection();
        services.AddSingleton(authService);
        services.AddDataProtection();
        var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        if (confirmed)
        {
            http.Request.Headers.Cookie = IntentCookie(provider, userId);
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>The cookie POST auth/oidc/link sets once the password checks out.</summary>
    private static string IntentCookie(IServiceProvider services, int userId)
    {
        var scratch = new DefaultHttpContext { RequestServices = services };
        OidcLinkIntent.Issue(scratch, userId);
        var setCookie = scratch.Response.Headers.SetCookie.ToString();
        return setCookie[..setCookie.IndexOf(';')];
    }

    [Fact]
    public async Task A_provider_error_on_link_is_a_redirect_not_an_unhandled_exception()
    {
        var userId = _db.SeedUser("ada");
        var controller = await ControllerAsync(userId, new ThrowingAuthenticationService());

        var result = await controller.OidcLink();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/settings?oidcLinkError=error.auth.ssoChallengeRejected", redirect.Url);
    }

    [Fact]
    public async Task Linking_without_a_confirmed_password_never_reaches_the_provider()
    {
        var userId = _db.SeedUser("ada");
        var controller = await ControllerAsync(userId, new ThrowingAuthenticationService(), confirmed: false);

        var result = await controller.OidcLink();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/settings?oidcLinkError=error.auth.ssoLinkNeedsPassword", redirect.Url);
    }

    [Fact]
    public async Task A_confirmation_minted_for_another_account_does_not_count()
    {
        var userId = _db.SeedUser("ada");
        var otherId = _db.SeedUser("mallory");
        var controller = await ControllerAsync(userId, new ThrowingAuthenticationService(), confirmed: false);
        controller.HttpContext.Request.Headers.Cookie = IntentCookie(controller.HttpContext.RequestServices, otherId);

        var result = await controller.OidcLink();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/settings?oidcLinkError=error.auth.ssoLinkNeedsPassword", redirect.Url);
    }

    [Fact]
    public async Task Oidc_disabled_still_answers_not_found_before_touching_the_handler()
    {
        var userId = _db.SeedUser("ada");
        // A handler that would fail the test if OidcLink ever reached it while OIDC is off.
        var controller = await ControllerAsync(userId, new ThrowingAuthenticationService());
        _db.SetConfig((SettingKeys.AuthOidcEnabled, "false"));
        var db = _db.NewContext(userId);
        var oidc = new OidcRuntimeOptions();
        await oidc.LoadAsync(db);
        var disabledController = new AuthController(
            new TestLocalizer(), db, null!, null!, null!, null!, new TestCurrentUser(userId),
            null!, oidc, null!, new StoppedClock(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<AuthController>.Instance, null!)
        {
            ControllerContext = controller.ControllerContext,
        };

        var result = await disabledController.OidcLink();

        Assert.IsType<NotFoundResult>(result);
    }
}
