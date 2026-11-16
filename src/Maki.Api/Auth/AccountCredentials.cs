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
    /// What minting an API key or OPDS token asks for. A password only when the account can actually
    /// sign in with one: a password hash that <c>auth.oidconly</c> refuses at login is one the owner
    /// has likely forgotten, and asking for it would leave them no way to mint at all.
    /// </summary>
    public static async Task<MintProof> MintProofForAsync(
        UserManager<MakiUser> users, OidcRuntimeOptions oidc, MakiUser user) =>
        await PasswordLoginAvailableAsync(users, oidc, user) ? MintProof.Password : MintProof.RecentSignIn;

    /// <summary>How much of <see cref="PasswordlessSignInWindow"/> is left since the last sign-in.</summary>
    public static TimeSpan RecentSignInLeft(MakiUser user, TimeProvider clock)
    {
        if (user.LastLoginAt is not { } at)
        {
            return TimeSpan.Zero;
        }

        var left = at + PasswordlessSignInWindow - clock.GetUtcNow().UtcDateTime;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// The check for minting something that outlives the session: an API key or an OPDS token. An
    /// account that signs in with a password confirms it. One that signs in only through single
    /// sign-on has nothing to type, so it must have signed in within
    /// <see cref="PasswordlessSignInWindow"/> instead, which keeps a stolen session from minting a
    /// durable credential on its own.
    /// </summary>
    public static async Task<string?> ConfirmForMintAsync(
        UserManager<MakiUser> users, SignInManager<MakiUser> signIn, OidcRuntimeOptions oidc, MakiUser user,
        string? password, TimeProvider clock)
    {
        if (await MintProofForAsync(users, oidc, user) == MintProof.Password)
        {
            return await ConfirmPasswordAsync(users, signIn, user, password, requirePassword: true);
        }

        return RecentSignInLeft(user, clock) > TimeSpan.Zero ? null : RecentSignInRequiredKey;
    }

    /// <summary>
    /// Whether the account can sign in with a password at all: it has one, and <c>auth.oidconly</c>
    /// does not refuse it (admins are exempt).
    /// </summary>
    public static async Task<bool> PasswordLoginAvailableAsync(
        UserManager<MakiUser> users, OidcRuntimeOptions oidc, MakiUser user) =>
        await users.HasPasswordAsync(user) && (!oidc.OidcOnly || user.Permissions.Grants(MakiPermission.Admin));
}

public enum MintProof
{
    Password,
    RecentSignIn
}
