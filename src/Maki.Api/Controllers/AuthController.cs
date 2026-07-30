using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Maki.Api.Controllers;

/// <summary>
/// Sign-in, sign-out, first-run setup, and "who am I".
/// <para>
/// Every failure answers with one generic message. Distinguishing "no such user" from "wrong
/// password" from "account disabled" turns the login form into an account enumerator, and on an
/// instance reachable from the internet that is the first thing an attacker asks it.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthController(
    MakiDbContext db,
    UserManager<MakiUser> userManager,
    SignInManager<MakiUser> signInManager,
    IPasswordHasher<MakiUser> passwordHasher,
    IAntiforgery antiforgery,
    ICurrentUser currentUser,
    AuthEventLogger auditLog,
    OidcRuntimeOptions oidc,
    OidcSignInService oidcSignIn,
    TimeProvider clock,
    ILogger<AuthController> logger) : ControllerBase
{
    private const string GenericFailure = "Invalid username or password";

    /// <summary>
    /// A real password hash to verify against when the username does not exist, so a miss costs the
    /// same PBKDF2 work as a hit. Without it, "unknown user" returns in microseconds while a wrong
    /// password takes the full 210,000 iterations — a difference an attacker can measure over the
    /// network and use to enumerate valid usernames.
    /// </summary>
    private static string? _dummyHash;

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == currentUser.UserId, ct);
        if (user is null)
        {
            return Unauthorized(new { error = "Unauthorized" });
        }

        return Ok(UserDtoMapper.ToMe(user, await RootFolderIdsAsync(user, ct), await OidcLinkedAsync(user)));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(request.Password))
        {
            return Unauthorized(new { error = GenericFailure });
        }

        var user = await userManager.FindByNameAsync(username);

        // PendingSetup is refused here rather than being allowed to fail on a null password hash:
        // the placeholder account the migration creates must be claimable only through /auth/setup.
        if (user is null || user.Disabled || user.PendingSetup)
        {
            BurnPasswordTime(request.Password);
            await auditLog.LogAsync(AuthEventType.LoginFailed, username, user?.Id, HttpContext,
                detail: user is null ? "no such user" : user.Disabled ? "account disabled" : "account unclaimed", ct: ct);
            return Unauthorized(new { error = GenericFailure });
        }

        // Local password login switched off for everyone but admins. Checked before the password is
        // verified, but after the same PBKDF2 time has been spent, so it stays indistinguishable from
        // every other failure — and so a refused account never accumulates lockout counts against a
        // password it was never allowed to use anyway.
        //
        // Admins are exempt unconditionally. An identity provider that is down, or whose client
        // secret has rotated, must not be able to lock the instance's owner out of their own library;
        // MAKI_ALLOW_LOCAL_LOGIN restores it for everyone else in the same situation.
        if (oidc.OidcOnly && !user.Permissions.Grants(MakiPermission.Admin))
        {
            BurnPasswordTime(request.Password);
            await auditLog.LogAsync(AuthEventType.LoginFailed, username, user.Id, HttpContext,
                detail: "password login disabled by auth.oidconly", ct: ct);
            return Unauthorized(new { error = GenericFailure });
        }

        var result = await signInManager.PasswordSignInAsync(
            user, request.Password, isPersistent: true, lockoutOnFailure: true);

        if (result.RequiresTwoFactor)
        {
            // PasswordSignInAsync has issued the short-lived two-factor cookie; the code goes to
            // POST auth/2fa. No session cookie exists yet.
            return Ok(new { requiresTwoFactor = true });
        }

        if (result.IsLockedOut)
        {
            await auditLog.LogAsync(AuthEventType.LockedOut, username, user.Id, HttpContext, ct: ct);
            // Still the generic message: confirming a lockout confirms the username exists.
            return Unauthorized(new { error = GenericFailure });
        }

        if (!result.Succeeded)
        {
            await auditLog.LogAsync(AuthEventType.LoginFailed, username, user.Id, HttpContext,
                detail: "wrong password", ct: ct);
            return Unauthorized(new { error = GenericFailure });
        }

        return Ok(await CompleteSignInAsync(user, ct));
    }

    [HttpPost("2fa")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> TwoFactor([FromBody] TwoFactorRequest request, CancellationToken ct)
    {
        var code = request.Code?.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (string.IsNullOrEmpty(code))
        {
            return Unauthorized(new { error = "Invalid code" });
        }

        // Reads the two-factor cookie PasswordSignInAsync set; null means that step never happened
        // or the cookie expired, so there is nothing to complete.
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null || user.Disabled || user.PendingSetup)
        {
            return Unauthorized(new { error = "Invalid code" });
        }

        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
            code, isPersistent: true, rememberClient: request.RememberMachine);

        if (!result.Succeeded)
        {
            await auditLog.LogAsync(AuthEventType.LoginFailed, user.UserName ?? string.Empty, user.Id,
                HttpContext, detail: result.IsLockedOut ? "locked out at 2fa" : "wrong 2fa code", ct: ct);
            return Unauthorized(new { error = "Invalid code" });
        }

        return Ok(await CompleteSignInAsync(user, ct));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var name = currentUser.UserName;
        var id = currentUser.UserId;
        await signInManager.SignOutAsync();
        await auditLog.LogAsync(AuthEventType.LoggedOut, name, id, HttpContext, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Claims the placeholder account the multi-user migration created, which is both how a fresh
    /// install gets its first admin and how an upgraded single-user install gets a login without
    /// losing anything: the placeholder is user 1, and every per-user row already points at it.
    /// <para>
    /// Open without authentication by necessity — there is no account to authenticate as yet. What
    /// keeps that from being a backdoor is that it only ever touches a row with
    /// <see cref="MakiUser.PendingSetup"/> set, and there is exactly one of those, only ever created
    /// by the migration. Once claimed, this endpoint can do nothing at all.
    /// </para>
    /// </summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Setup([FromBody] SetupRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.PendingSetup, ct);
        if (user is null)
        {
            return Conflict(new { error = "Setup has already been completed" });
        }

        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new { error = "Password is required" });
        }

        // Rename before setting the password so a rejected password leaves the account untouched
        // rather than half-renamed. SetUserNameAsync also refreshes NormalizedUserName, which the
        // unique index and every lookup depend on.
        if (!string.Equals(user.UserName, username, StringComparison.Ordinal))
        {
            var rename = await userManager.SetUserNameAsync(user, username);
            if (!rename.Succeeded)
            {
                return BadRequest(new { error = Describe(rename) });
            }
        }

        var added = await userManager.AddPasswordAsync(user, request.Password);
        if (!added.Succeeded)
        {
            return BadRequest(new { error = Describe(added) });
        }

        user.PendingSetup = false;
        user.DisplayName = request.DisplayName?.Trim();
        // Belt and braces: the migration already set these, but setup is what makes the account
        // real and an admin that cannot administer would be unrecoverable.
        user.Permissions |= MakiPermission.Admin;
        user.AllRootFolders = true;
        if (!ContentRating.IsValid(user.MaxContentRating))
        {
            user.MaxContentRating = ContentRating.Default;
        }

        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return BadRequest(new { error = Describe(updated) });
        }

        await signInManager.SignInAsync(user, isPersistent: true);
        await auditLog.LogAsync(AuthEventType.SetupCompleted, username, user.Id, HttpContext, ct: ct);
        logger.LogInformation("First-run setup completed for {UserName}", username);

        return Ok(await CompleteSignInAsync(user, ct, alreadySignedIn: true));
    }

    /// <summary>
    /// Starts a single sign-on. A plain redirect rather than a fetch: the browser has to leave the
    /// origin entirely, and the SPA links to this URL instead of calling it.
    /// </summary>
    [HttpGet("oidc/challenge")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public IActionResult OidcChallenge([FromQuery] string? returnUrl)
    {
        if (!oidc.Enabled)
        {
            // 404 rather than 400: an instance with no provider configured should not confirm that
            // this endpoint is one of the things it has.
            return NotFound();
        }

        var target = LocalOrRoot(returnUrl);
        var properties = new AuthenticationProperties
        {
            // Where the handler sends the browser once the code has been exchanged and the result
            // deposited in the external cookie. Deliberately not OidcRuntimeOptions.CallbackPath:
            // that path is intercepted by the OIDC middleware itself on every request that matches
            // it, before routing ever sees it, so a second hop to the same path would re-enter the
            // handler with no code/state and fail with "message.State is null or empty" instead of
            // ever reaching OidcCallback below.
            RedirectUri = $"/api/v1/auth/oidc/complete?returnUrl={Uri.EscapeDataString(target)}"
        };

        return Challenge(properties, AuthSchemes.Oidc);
    }

    /// <summary>
    /// Finishes a single sign-on: reads the provider's result out of the short-lived external cookie,
    /// resolves it to a Maki account, and issues the real session.
    /// <para>
    /// Anonymous by necessity — the caller has no Maki session yet, which is the point. What
    /// authenticates them is the external cookie, which only the OpenID Connect handler can write and
    /// only after it has validated a signed token against the provider's published keys.
    /// </para>
    /// </summary>
    [HttpGet("oidc/complete")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> OidcCallback([FromQuery] string? returnUrl, CancellationToken ct)
    {
        if (!oidc.Enabled)
        {
            return NotFound();
        }

        var external = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);

        // Read once, then dropped whatever happens next. It is a single-use handover, and leaving it
        // set would let a failed sign-in be retried by reloading the URL.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        if (!external.Succeeded || external.Principal is null)
        {
            return SsoFailure("The sign-in did not complete. Please try again.");
        }

        var claims = external.Principal.Claims.ToList();

        // Read directly rather than through SignInManager.GetExternalLoginInfoAsync, which looks for
        // ClaimTypes.NameIdentifier. MapInboundClaims is off so that claims keep the names the
        // provider actually sent — "groups" and "email", not the SOAP-era schema URIs — because the
        // permission and admin claims are configured by name, and a renamed claim would silently
        // match nothing. The cost is resolving the subject here.
        var subject = external.Principal.FindFirstValue("sub")
            ?? external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(subject))
        {
            logger.LogWarning("Single sign-on returned no subject claim");
            return SsoFailure("The identity provider returned no subject");
        }

        var resolved = await oidcSignIn.SignInAsync(AuthSchemes.Oidc, subject, claims, ct);
        if (resolved.User is null)
        {
            await auditLog.LogAsync(AuthEventType.LoginFailed,
                OidcClaimMapper.UserName(oidc, claims, subject), null, HttpContext,
                detail: $"single sign-on refused: {resolved.Error}", ct: ct);
            return SsoFailure(resolved.Error ?? "Sign-in failed");
        }

        var user = resolved.User;
        await signInManager.SignInAsync(user, isPersistent: true);

        if (resolved.Provisioned || resolved.Linked)
        {
            await auditLog.LogAsync(
                resolved.Provisioned ? AuthEventType.OidcProvisioned : AuthEventType.OidcLinked,
                user.UserName ?? string.Empty, user.Id, HttpContext,
                detail: $"subject {subject}", ct: ct);
        }

        user.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await auditLog.LogAsync(AuthEventType.LoginSucceeded, user.UserName ?? string.Empty, user.Id,
            HttpContext, detail: "single sign-on", ct: ct);

        // No antiforgery token is issued here: this is a redirect, and the SPA load that follows it is
        // a GET, which AntiforgeryTokenMiddleware reissues on unconditionally.
        return Redirect(LocalOrRoot(returnUrl));
    }

    /// <summary>
    /// Starts linking single sign-on to the account already signed in with a password — the
    /// self-service counterpart to <see cref="MatchByEmailAsync"/>'s automatic link, for an operator
    /// whose provider does not send a verified email or whose Maki account uses a different one.
    /// <para>
    /// No explicit CSRF defence beyond what the OIDC handler already does: an attacker who sends a
    /// signed-in victim this link cannot supply their own authorization code through it, because the
    /// callback is only accepted alongside the correlation cookie this exact challenge set — the
    /// state/PKCE round trip that already defends the ordinary sign-in flow defends this one too.
    /// </para>
    /// </summary>
    [HttpGet("oidc/link")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public IActionResult OidcLink()
    {
        if (!oidc.Enabled)
        {
            return NotFound();
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = "/api/v1/auth/oidc/link-complete"
        };

        return Challenge(properties, AuthSchemes.Oidc);
    }

    /// <summary>
    /// Finishes linking. Requires the caller to still be signed in with their own session — the
    /// Maki.Session cookie rides along on this top-level GET the same way it does on any other
    /// same-site navigation — so the account gaining the login is whoever asked, not whoever the
    /// external cookie says signed in.
    /// </summary>
    [HttpGet("oidc/link-complete")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> OidcLinkComplete(CancellationToken ct)
    {
        if (!oidc.Enabled)
        {
            return NotFound();
        }

        var external = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        if (!external.Succeeded || external.Principal is null)
        {
            return LinkFailure("The sign-in did not complete. Please try again.");
        }

        var subject = external.Principal.FindFirstValue("sub")
            ?? external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(subject))
        {
            return LinkFailure("The identity provider returned no subject");
        }

        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var existing = await userManager.FindByLoginAsync(AuthSchemes.Oidc, subject);
        if (existing is not null && existing.Id != user.Id)
        {
            // Refused rather than re-linked: moving it here would silently strip the login from
            // whoever it belonged to before.
            return LinkFailure("That single sign-on account is already linked to a different user");
        }

        if (existing is null)
        {
            var result = await userManager.AddLoginAsync(
                user, new UserLoginInfo(AuthSchemes.Oidc, subject, AuthSchemes.Oidc));
            if (!result.Succeeded)
            {
                logger.LogWarning("Could not link single sign-on to {UserName}: {Errors}",
                    user.UserName, string.Join("; ", result.Errors.Select(e => e.Description)));
                return LinkFailure("Could not link that single sign-on account");
            }

            await auditLog.LogAsync(AuthEventType.OidcLinked, user.UserName ?? string.Empty, user.Id,
                HttpContext, detail: $"subject {subject}", ct: ct);
        }

        return Redirect("/settings?oidcLinked=1");
    }

    /// <summary>
    /// Back to the login page with the reason in the query string. A redirect rather than a JSON
    /// error because the browser got here by a top-level navigation from the provider — there is no
    /// fetch waiting for a response body.
    /// </summary>
    private IActionResult SsoFailure(string message) =>
        Redirect("/login?ssoError=" + Uri.EscapeDataString(message));

    /// <summary>Same idea as <see cref="SsoFailure"/>, back to the settings page instead.</summary>
    private IActionResult LinkFailure(string message) =>
        Redirect("/settings?oidcLinkError=" + Uri.EscapeDataString(message));

    /// <summary>
    /// Refuses anything that is not a path on this instance. Without it the return URL is an open
    /// redirect: an attacker sends a victim through a genuine Maki sign-in and lands them, freshly
    /// authenticated and trusting, on a page of the attacker's choosing.
    /// </summary>
    private string LocalOrRoot(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

    private async Task<MeDto> CompleteSignInAsync(MakiUser user, CancellationToken ct, bool alreadySignedIn = false)
    {
        user.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        // Hand back an antiforgery token bound to the identity the caller now has.
        //
        // A token is bound to the principal it was issued to, and the one the browser is holding was
        // issued while it was anonymous — so without this the very first mutation after signing in
        // fails with "invalid antiforgery token" until some GET happens to reissue. SignInAsync does
        // not update HttpContext.User within the request that calls it, so the principal is set here
        // first; otherwise the new token would be bound to the anonymous identity all over again.
        HttpContext.User = await signInManager.CreateUserPrincipalAsync(user);
        AntiforgeryTokenMiddleware.IssueToken(HttpContext, antiforgery);

        if (!alreadySignedIn)
        {
            await auditLog.LogAsync(AuthEventType.LoginSucceeded, user.UserName ?? string.Empty,
                user.Id, HttpContext, ct: ct);
        }

        return UserDtoMapper.ToMe(user, await RootFolderIdsAsync(user, ct), await OidcLinkedAsync(user));
    }

    private async Task<IReadOnlyList<int>> RootFolderIdsAsync(MakiUser user, CancellationToken ct) =>
        user.AllRootFolders
            ? await db.RootFolders.Select(r => r.Id).ToListAsync(ct)
            : await db.UserRootFolders.Where(g => g.UserId == user.Id).Select(g => g.RootFolderId).ToListAsync(ct);

    /// <summary>Whether the account can already sign in through the provider, for the settings page.</summary>
    private async Task<bool> OidcLinkedAsync(MakiUser user) =>
        (await userManager.GetLoginsAsync(user)).Any(l => l.LoginProvider == AuthSchemes.Oidc);

    /// <summary>
    /// Spends the same PBKDF2 time a real verification would, so a failed lookup is not measurably
    /// faster than a failed password. The hash is built once per process with the *injected* hasher,
    /// so it carries the configured iteration count rather than the framework default.
    /// </summary>
    private void BurnPasswordTime(string password)
    {
        _dummyHash ??= passwordHasher.HashPassword(new MakiUser(), Guid.NewGuid().ToString());
        passwordHasher.VerifyHashedPassword(new MakiUser(), _dummyHash, password);
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
