using System.Security.Claims;
using Maki.Api.Services;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <param name="User">The account to sign in, or null when <paramref name="Error"/> says why not.</param>
/// <param name="Error">
/// Shown to the user on the login page. Deliberately vague about *which* account is involved — this
/// endpoint is reachable by anyone who can reach the identity provider.
/// </param>
/// <param name="Linked">An existing local account gained this provider login on this request.</param>
/// <param name="Provisioned">The account was created on this request.</param>
public sealed record OidcSignInResult(MakiUser? User, string? Error, bool Linked = false, bool Provisioned = false)
{
    public static OidcSignInResult Fail(string error) => new(null, error);
}

/// <summary>
/// Resolves a completed OpenID Connect login to a Maki account: match, link, optionally provision,
/// and apply whatever the provider says about permissions.
/// <para>
/// Separate from the controller so the rules can be tested against a real database without a
/// provider or a browser round trip. The controller's job is only the redirects.
/// </para>
/// </summary>
public class OidcSignInService(
    MakiDbContext db,
    UserManager<MakiUser> userManager,
    OidcRuntimeOptions options,
    TimeProvider clock,
    ILogger<OidcSignInService> logger)
{
    /// <param name="provider">The login provider name stored in <c>AspNetUserLogins</c>.</param>
    /// <param name="subject">
    /// The provider's <c>sub</c> claim. The only durable identifier — usernames and email addresses
    /// both change, and matching on either alone is how one person ends up holding another's library.
    /// </param>
    public async Task<OidcSignInResult> SignInAsync(
        string provider, string subject, IReadOnlyCollection<Claim> claims, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return OidcSignInResult.Fail("The identity provider returned no subject");
        }

        var user = await userManager.FindByLoginAsync(provider, subject);
        var linked = false;

        if (user is null)
        {
            (user, linked) = await MatchByEmailAsync(provider, subject, claims, ct);
        }

        if (user is null)
        {
            if (!options.AutoProvision)
            {
                logger.LogWarning("Rejected single sign-on for an unknown subject; auto-provisioning is off");
                return OidcSignInResult.Fail("No Maki account is linked to that login");
            }

            return await ProvisionAsync(provider, subject, claims, ct);
        }

        if (user.Disabled)
        {
            return OidcSignInResult.Fail("That account is disabled");
        }

        // The placeholder the multi-user migration inserts owns the entire pre-upgrade library. Only
        // POST auth/setup may claim it: otherwise the first person the provider will authenticate —
        // which with auto-provisioning on is anyone in the realm — walks into somebody else's
        // library as an admin.
        if (user.PendingSetup)
        {
            return OidcSignInResult.Fail("That account has not been set up yet");
        }

        await ApplyClaimsAsync(user, provider, subject, claims, ct);
        return new OidcSignInResult(user, null, Linked: linked);
    }

    /// <summary>
    /// Links an incoming subject to an existing local account with the same <b>verified</b> email.
    /// <para>
    /// This is the upgrade path — an instance whose users already have passwords should not have to
    /// abandon their reading history to move to single sign-on. It is also the one place where
    /// something other than the subject decides who somebody is, which is why the address has to be
    /// verified by the provider and has to match exactly one account.
    /// </para>
    /// </summary>
    private async Task<(MakiUser? User, bool Linked)> MatchByEmailAsync(
        string provider, string subject, IReadOnlyCollection<Claim> claims, CancellationToken ct)
    {
        var email = OidcClaimMapper.Email(claims);
        if (email is null || !OidcClaimMapper.EmailVerified(claims))
        {
            return (null, false);
        }

        var normalized = userManager.NormalizeEmail(email);
        var matches = await db.Users.Where(u => u.NormalizedEmail == normalized).Take(2).ToListAsync(ct);

        // Email is not unique in this schema (RequireUniqueEmail is off — this is a self-hosted app
        // with no mail server), so two accounts can legitimately share an address. Linking to a
        // guess would be a coin flip over whose library the caller gets.
        if (matches.Count != 1)
        {
            return (null, false);
        }

        var user = matches[0];
        if (user.PendingSetup)
        {
            return (null, false);
        }

        var displayName = OidcClaimMapper.UserName(options, claims, subject);
        var result = await userManager.AddLoginAsync(user, new UserLoginInfo(provider, subject, displayName));
        if (!result.Succeeded)
        {
            logger.LogWarning("Could not link single sign-on to {UserName}: {Errors}",
                user.UserName, string.Join("; ", result.Errors.Select(e => e.Description)));
            return (null, false);
        }

        logger.LogInformation("Linked single sign-on to existing account {UserName} by verified email",
            user.UserName);
        return (user, true);
    }

    private async Task<OidcSignInResult> ProvisionAsync(
        string provider, string subject, IReadOnlyCollection<Claim> claims, CancellationToken ct)
    {
        var userName = OidcClaimMapper.UserName(options, claims, subject);

        // A name collision is refused rather than resolved by suffixing or by linking: linking would
        // hand the new subject an existing person's library, and a silent "reader2" is a support
        // question nobody can answer later.
        if (await db.Users.AnyAsync(u => u.NormalizedUserName == userManager.NormalizeName(userName), ct))
        {
            logger.LogWarning("Refused to provision {UserName} — an account with that name already exists", userName);
            return OidcSignInResult.Fail("An account with that username already exists");
        }

        var user = new MakiUser
        {
            UserName = userName,
            Email = OidcClaimMapper.Email(claims),
            EmailConfirmed = OidcClaimMapper.EmailVerified(claims),
            DisplayName = OidcClaimMapper.DisplayName(claims),
            Permissions = OidcClaimMapper.Map(options, claims, MakiPermissions.DefaultForNewUser),
            MaxContentRating = ContentRating.Safe,
            // Fail closed, exactly as a hand-created user does: an empty library until an admin
            // grants a root folder. The provider says who somebody is, never what they may read.
            AllRootFolders = false,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };

        // No password. PendingSetup stays false — that flag means "unclaimed placeholder", and this
        // account is fully real; it simply signs in through the provider.
        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            var detail = string.Join("; ", created.Errors.Select(e => e.Description));
            logger.LogWarning("Could not provision {UserName}: {Errors}", userName, detail);
            return OidcSignInResult.Fail(detail);
        }

        var linked = await userManager.AddLoginAsync(user, new UserLoginInfo(provider, subject, userName));
        if (!linked.Succeeded)
        {
            // Without the link the account could never be signed into again and would block the name
            // forever, so it does not get to exist half-made.
            await userManager.DeleteAsync(user);
            return OidcSignInResult.Fail("Could not link that login to a new account");
        }

        // Nobody is signed in yet, so the id is passed explicitly rather than read off the scope.
        await ReadingProfileSeeder.SeedAsync(db, user.Id, ct);

        logger.LogInformation("Provisioned {UserName} from single sign-on with {Permissions}",
            userName, user.Permissions);
        return new OidcSignInResult(user, null, Provisioned: true);
    }

    /// <summary>
    /// Re-applies the provider's view of this user on every sign-in, but only where the operator has
    /// said the provider is the authority. See <see cref="OidcRuntimeOptions.MapsPermissions"/>.
    /// </summary>
    private async Task ApplyClaimsAsync(
        MakiUser user, string provider, string subject, IReadOnlyCollection<Claim> claims, CancellationToken ct)
    {
        var changed = false;

        if (options.MapsPermissions)
        {
            var mapped = OidcClaimMapper.Map(options, claims, user.Permissions);
            if (mapped != user.Permissions)
            {
                logger.LogInformation("Single sign-on changed {UserName} from {Before} to {After}",
                    user.UserName, user.Permissions, mapped);
                user.Permissions = mapped;
                changed = true;
            }
        }

        var email = OidcClaimMapper.Email(claims);
        if (email is not null && !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            user.Email = email;
            user.NormalizedEmail = userManager.NormalizeEmail(email);
            user.EmailConfirmed = OidcClaimMapper.EmailVerified(claims);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        // ProviderDisplayName is set once, at whichever sign-in first created this login row, and
        // Identity has no update path for it — only remove-and-re-add. Left alone, a name resolved
        // from a thin claim set (the provider sent no preferred_username/name/email that day, so it
        // fell all the way back to the raw subject) stays wrong forever, even after the provider
        // starts sending better claims. Recomputing here self-heals it the same way email already
        // does above.
        var freshName = OidcClaimMapper.UserName(options, claims, subject);
        var logins = await userManager.GetLoginsAsync(user);
        var current = logins.FirstOrDefault(l => l.LoginProvider == provider && l.ProviderKey == subject);
        if (current is not null && !string.Equals(current.ProviderDisplayName, freshName, StringComparison.Ordinal))
        {
            await userManager.RemoveLoginAsync(user, provider, subject);
            await userManager.AddLoginAsync(user, new UserLoginInfo(provider, subject, freshName));
        }
    }
}
