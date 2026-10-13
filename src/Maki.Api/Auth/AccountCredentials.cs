using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity;

namespace Maki.Api.Auth;

/// <summary>
/// The password checks every controller that guards an account action shares, so a wrong password
/// counts toward lockout and a locked-out account gets the same answer everywhere.
/// </summary>
public static class AccountCredentials
{
    /// <summary>
    /// Null when <paramref name="password"/> is the account's password, otherwise the catalogue key to
    /// refuse with. An account with no password has nothing to confirm and passes, unless
    /// <paramref name="requirePassword"/> says the action needs one regardless.
    /// </summary>
    public static async Task<string?> ConfirmPasswordAsync(
        UserManager<MakiUser> users, SignInManager<MakiUser> signIn, MakiUser user, string? password,
        bool requirePassword = false)
    {
        if (!requirePassword && !await users.HasPasswordAsync(user))
        {
            return null;
        }

        if (string.IsNullOrEmpty(password))
        {
            return "error.account.incorrectPassword";
        }

        var check = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (check.IsLockedOut)
        {
            return "error.account.lockedOut";
        }

        return check.Succeeded ? null : "error.account.incorrectPassword";
    }

    public const string RecentSignInRequiredKey = "error.account.recentSignInRequired";

    /// <summary>How recent a sign-in a passwordless account needs before it may mint a key or token.</summary>
    public static readonly TimeSpan PasswordlessSignInWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The check for minting something that outlives the session: an API key or an OPDS token. An
    /// account with a password confirms it as usual. One without (single sign-on only) has nothing to
    /// type, so it must have signed in within <see cref="PasswordlessSignInWindow"/> instead, which
    /// keeps a stolen session from minting a durable credential on its own.
    /// </summary>
    public static async Task<string?> ConfirmForMintAsync(
        UserManager<MakiUser> users, SignInManager<MakiUser> signIn, MakiUser user, string? password,
        TimeProvider clock)
    {
        if (await users.HasPasswordAsync(user))
        {
            return await ConfirmPasswordAsync(users, signIn, user, password, requirePassword: true);
        }

        return user.LastLoginAt is { } at && clock.GetUtcNow().UtcDateTime - at <= PasswordlessSignInWindow
            ? null
            : RecentSignInRequiredKey;
    }

    /// <summary>
    /// Whether the account can sign in with a password at all: it has one, and <c>auth.oidconly</c>
    /// does not refuse it (admins are exempt).
    /// </summary>
    public static async Task<bool> PasswordLoginAvailableAsync(
        UserManager<MakiUser> users, OidcRuntimeOptions oidc, MakiUser user) =>
        await users.HasPasswordAsync(user) && (!oidc.OidcOnly || user.Permissions.Grants(MakiPermission.Admin));
}
