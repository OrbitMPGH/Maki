using System.Text;
using System.Text.Encodings.Web;
using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// What a user manages about their own account: password, two-factor, API keys, and signing every
/// other session out. Nothing here needs a permission — it is all self-service — but everything is
/// scoped to <see cref="ICurrentUser.UserId"/> and never accepts a user id from the request.
/// </summary>
[ApiController]
[Route("api/v1/account")]
public class AccountController(
    MakiDbContext db,
    UserManager<MakiUser> userManager,
    SignInManager<MakiUser> signInManager,
    ICurrentUser currentUser,
    AuthEventLogger auditLog,
    TimeProvider clock) : ControllerBase
{
    private const int RecoveryCodeCount = 8;

    private async Task<MakiUser?> LoadAsync() =>
        await userManager.FindByIdAsync(currentUser.UserId.ToString());

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return BadRequest(new { error = "Current and new password are required" });
        }

        var user = await LoadAsync();
        if (user is null) return Unauthorized();

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return BadRequest(new { error = Describe(result) });
        }

        // ChangePasswordAsync rotates the security stamp, which invalidates every issued cookie —
        // including the one making this request. Re-issuing it here keeps the user signed in on this
        // device while every other session dies, which is the behaviour a password change should have.
        await signInManager.RefreshSignInAsync(user);
        await auditLog.LogAsync(AuthEventType.PasswordChanged, user.UserName ?? string.Empty, user.Id, HttpContext, ct: ct);
        return NoContent();
    }

    [HttpGet("2fa")]
    public async Task<IActionResult> TwoFactorStatus()
    {
        var user = await LoadAsync();
        if (user is null) return Unauthorized();

        return Ok(new
        {
            enabled = user.TwoFactorEnabled,
            hasAuthenticator = await userManager.GetAuthenticatorKeyAsync(user) is { Length: > 0 },
            recoveryCodesLeft = await userManager.CountRecoveryCodesAsync(user)
        });
    }

    /// <summary>
    /// Issues (or reissues) the shared secret and the <c>otpauth://</c> URI the authenticator app
    /// scans. Enabling is a separate call that requires a working code — otherwise a user could lock
    /// themselves out of their own account by enrolling a secret they never successfully scanned.
    /// </summary>
    [HttpPost("2fa/setup")]
    public async Task<IActionResult> SetupTwoFactor()
    {
        var user = await LoadAsync();
        if (user is null) return Unauthorized();

        if (user.TwoFactorEnabled)
        {
            return Conflict(new { error = "Two-factor authentication is already enabled" });
        }

        // Always a fresh secret: reusing one across abandoned enrolment attempts means an old QR
        // screenshot still works.
        await userManager.ResetAuthenticatorKeyAsync(user);
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Could not generate an authenticator key" });
        }

        var label = UrlEncoder.Default.Encode(user.UserName ?? "user");
        var uri = $"otpauth://totp/Maki:{label}?secret={key}&issuer=Maki&digits=6";

        return Ok(new TwoFactorSetupDto(FormatKey(key), uri));
    }

    [HttpPost("2fa/enable")]
    public async Task<IActionResult> EnableTwoFactor([FromBody] EnableTwoFactorRequest request, CancellationToken ct)
    {
        var user = await LoadAsync();
        if (user is null) return Unauthorized();

        var code = request.Code?.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest(new { error = "A code from your authenticator app is required" });
        }

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!valid)
        {
            return BadRequest(new { error = "That code is not valid" });
        }

        await userManager.SetTwoFactorEnabledAsync(user, true);
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        await auditLog.LogAsync(AuthEventType.TwoFactorEnabled, user.UserName ?? string.Empty, user.Id, HttpContext, ct: ct);

        // Shown once. Identity stores them hashed, so there is no second chance to read them.
        return Ok(new { recoveryCodes = codes ?? [] });
    }

    /// <summary>
    /// Requires the account password. Turning off a second factor is exactly the action a hijacked
    /// session would want, so it must not be reachable with the session cookie alone.
    /// </summary>
    [HttpPost("2fa/disable")]
    public async Task<IActionResult> DisableTwoFactor([FromBody] DisableTwoFactorRequest request, CancellationToken ct)
    {
        var user = await LoadAsync();
        if (user is null) return Unauthorized();

        if (string.IsNullOrEmpty(request.Password) ||
            !await userManager.CheckPasswordAsync(user, request.Password))
        {
            return BadRequest(new { error = "Password is incorrect" });
        }

        await userManager.SetTwoFactorEnabledAsync(user, false);
        // Clear the secret too, so re-enabling forces a fresh enrolment rather than silently
        // reactivating whatever app still has the old one.
        await userManager.ResetAuthenticatorKeyAsync(user);
        await auditLog.LogAsync(AuthEventType.TwoFactorDisabled, user.UserName ?? string.Empty, user.Id, HttpContext, ct: ct);
        return NoContent();
    }

    [HttpGet("apikeys")]
    public async Task<IActionResult> ListApiKeys(CancellationToken ct)
    {
        var keys = await db.UserApiKeys
            .AsNoTracking()
            .Where(k => k.UserId == currentUser.UserId)
            .OrderByDescending(k => k.Id)
            .Select(k => new ApiKeyDto(k.Id, k.Name, k.Prefix, k.Scope, k.CreatedAt, k.LastUsedAt, k.RevokedAt))
            .ToListAsync(ct);

        return Ok(keys);
    }

    [HttpPost("apikeys")]
    public async Task<IActionResult> CreateApiKey([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        // An OPDS token hands out the whole library to a third-party app, so it is gated on the same
        // permission as using OPDS at all rather than being available to anyone with an account.
        if (request.Scope == UserApiKeyScope.Opds && !currentUser.Has(MakiPermission.UseOpds))
        {
            return Forbid();
        }

        var secret = ApiKeyCrypto.Generate();
        var key = new UserApiKey
        {
            UserId = currentUser.UserId,
            Name = name,
            KeyHash = ApiKeyCrypto.Hash(secret),
            Prefix = ApiKeyCrypto.Prefix(secret),
            Scope = request.Scope,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };

        db.UserApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        await auditLog.LogAsync(AuthEventType.ApiKeyCreated, currentUser.UserName, currentUser.UserId,
            HttpContext, detail: $"{request.Scope} key \"{name}\"", ct: ct);

        return Ok(new CreatedApiKeyDto(
            new ApiKeyDto(key.Id, key.Name, key.Prefix, key.Scope, key.CreatedAt, null, null),
            secret));
    }

    [HttpDelete("apikeys/{id:int}")]
    public async Task<IActionResult> RevokeApiKey(int id, CancellationToken ct)
    {
        var key = await db.UserApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == currentUser.UserId, ct);
        if (key is null)
        {
            return NotFound();
        }

        if (key.RevokedAt is null)
        {
            // Revoked, not deleted: the row stays visible in the UI and keeps the audit trail
            // meaningful. A revoked key authenticates nothing.
            key.RevokedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await auditLog.LogAsync(AuthEventType.ApiKeyRevoked, currentUser.UserName, currentUser.UserId,
                HttpContext, detail: $"{key.Scope} key \"{key.Name}\"", ct: ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Signs out every session including this one's siblings, by rotating the security stamp that
    /// every issued cookie is validated against. Takes effect within the stamp validator's interval.
    /// </summary>
    [HttpPost("sessions/revoke-all")]
    public async Task<IActionResult> RevokeSessions(CancellationToken ct)
    {
        var user = await LoadAsync();
        if (user is null) return Unauthorized();

        await userManager.UpdateSecurityStampAsync(user);
        // Keep the caller signed in on this device — otherwise "sign out everywhere" also signs you
        // out here, which reads as a bug rather than a feature.
        await signInManager.RefreshSignInAsync(user);
        await auditLog.LogAsync(AuthEventType.SessionsRevoked, user.UserName ?? string.Empty, user.Id, HttpContext, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// The user's own content rating ceiling. Requires
    /// <see cref="MakiPermission.ChangeContentRating"/> — an admin can always set it for them, which
    /// is what makes a locked-down account for a child possible.
    /// </summary>
    [HttpPut("contentrating")]
    public async Task<IActionResult> SetContentRating([FromBody] string? rating, CancellationToken ct)
    {
        if (!currentUser.Has(MakiPermission.ChangeContentRating))
        {
            return Forbid();
        }

        if (!ContentRating.IsValid(rating))
        {
            return BadRequest(new { error = $"Rating must be one of: {string.Join(", ", ContentRating.All)}" });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.UserId, ct);
        if (user is null) return Unauthorized();

        user.MaxContentRating = rating!;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Groups the base32 secret so it can be typed by hand when a QR code cannot be scanned.</summary>
    private static string FormatKey(string key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (i > 0) result.Append(' ');
            result.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }
        return result.ToString().ToLowerInvariant();
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
