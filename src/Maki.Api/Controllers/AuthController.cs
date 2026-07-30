using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

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

        return Ok(UserDtoMapper.ToMe(user, await RootFolderIdsAsync(user, ct)));
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

        return UserDtoMapper.ToMe(user, await RootFolderIdsAsync(user, ct));
    }

    private async Task<IReadOnlyList<int>> RootFolderIdsAsync(MakiUser user, CancellationToken ct) =>
        user.AllRootFolders
            ? await db.RootFolders.Select(r => r.Id).ToListAsync(ct)
            : await db.UserRootFolders.Where(g => g.UserId == user.Id).Select(g => g.RootFolderId).ToListAsync(ct);

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
