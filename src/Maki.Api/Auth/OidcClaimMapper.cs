using System.Security.Claims;
using Maki.Core.Security;

namespace Maki.Api.Auth;

/// <summary>
/// Turns an identity provider's claims into Maki permissions and an account name.
/// <para>
/// Pure and static so the mapping rules can be tested without a provider, a handler or a database —
/// they are the part of single sign-on that decides what somebody may do, and getting them wrong is
/// silent.
/// </para>
/// </summary>
public static class OidcClaimMapper
{
    /// <summary>
    /// The permissions a user should hold after this sign-in.
    /// <para>
    /// When neither claim mapping is configured the provider says nothing about authorization and
    /// <paramref name="current"/> is returned untouched — that is the common case, where Maki's own
    /// user list is the authority and single sign-on only replaces the password. When either is
    /// configured the result is computed from the claims alone, so removing somebody from a group in
    /// the provider actually removes the permission here.
    /// </para>
    /// </summary>
    public static MakiPermission Map(
        OidcRuntimeOptions options, IReadOnlyCollection<Claim> claims, MakiPermission current)
    {
        if (!options.MapsPermissions)
        {
            return current;
        }

        if (options.AdminClaim?.IsSatisfiedBy(claims) == true)
        {
            // Admin implies every other permission, so it is stored on its own — the same shape the
            // user editor produces. See MakiPermissions.Grants.
            return MakiPermission.Admin;
        }

        var mapped = MakiPermission.None;
        if (options.PermissionClaim.Length == 0)
        {
            return mapped;
        }

        foreach (var claim in claims)
        {
            if (!string.Equals(claim.Type, options.PermissionClaim, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A value naming nothing grants nothing: a "groups" claim is full of values that have
            // nothing to do with Maki, and every one of them must be inert rather than an error.
            if (!Enum.TryParse<MakiPermission>(claim.Value, ignoreCase: true, out var permission))
            {
                continue;
            }

            // Admin comes from its own dedicated setting only. Honouring it here would mean any
            // provider whose group names happen to include "Admin" hands out the whole instance,
            // and the operator who configured a permission claim would have no way to say otherwise.
            if (permission is MakiPermission.None or MakiPermission.Admin)
            {
                continue;
            }

            mapped |= permission;
        }

        return mapped;
    }

    /// <summary>
    /// The account name to create a provisioned user under: the configured username claim, then any
    /// email, then the subject. The subject is ugly but unique and always present, and a name is
    /// cosmetic — the durable link is always the subject in <c>AspNetUserLogins</c>.
    /// </summary>
    public static string UserName(
        OidcRuntimeOptions options, IReadOnlyCollection<Claim> claims, string subject)
    {
        var candidate = Find(claims, options.UsernameClaim)
            ?? Find(claims, "preferred_username")
            ?? Find(claims, ClaimTypes.Name)
            ?? Email(claims)
            ?? subject;

        // Identity's default validator allows letters, digits and -._@+ only; anything else would
        // fail CreateAsync with a message about characters the user never typed.
        var cleaned = new string(candidate.Where(c => char.IsLetterOrDigit(c) || "-._@+".Contains(c)).ToArray());
        return cleaned.Length > 0 ? cleaned : subject;
    }

    public static string? Email(IReadOnlyCollection<Claim> claims) =>
        Find(claims, "email") ?? Find(claims, ClaimTypes.Email);

    /// <summary>
    /// Whether the provider states the address has been verified. Matching an incoming login to an
    /// existing local account by email is an account takeover whenever the provider lets a user set
    /// an arbitrary unverified address, so an absent or false <c>email_verified</c> means no match.
    /// </summary>
    public static bool EmailVerified(IReadOnlyCollection<Claim> claims) =>
        string.Equals(Find(claims, "email_verified"), "true", StringComparison.OrdinalIgnoreCase);

    public static string? DisplayName(IReadOnlyCollection<Claim> claims) =>
        Find(claims, "name") ?? Find(claims, ClaimTypes.Name);

    private static string? Find(IReadOnlyCollection<Claim> claims, string type)
    {
        foreach (var claim in claims)
        {
            if (string.Equals(claim.Type, type, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(claim.Value))
            {
                return claim.Value.Trim();
            }
        }

        return null;
    }
}
