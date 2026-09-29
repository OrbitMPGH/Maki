using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace Maki.Api.Auth;

/// <summary>
/// Proof that the account starting a single sign-on link confirmed its password a moment ago.
/// <para>
/// Linking is a top-level navigation to the provider, so the password cannot ride along with it:
/// <c>POST auth/oidc/link</c> checks it and sets this short-lived HttpOnly cookie, and
/// <c>GET auth/oidc/link</c> refuses to start without it. Without the check, a stolen session could
/// attach the thief's own provider account, and that login skips local 2FA and survives a password
/// change and "sign out everywhere".
/// </para>
/// <para>
/// The link-start also stamps the user id into the challenge's properties, which the handler
/// round-trips to <c>link-complete</c>. That is what stops the ordinary anonymous sign-in challenge
/// from being redirected at <c>link-complete</c> by hand to skip this step.
/// </para>
/// </summary>
public static class OidcLinkIntent
{
    public const string CookieName = "Maki.OidcLinkIntent";

    /// <summary>The key in <c>AuthenticationProperties.Items</c> that names the linking account.</summary>
    public const string PropertyKey = "maki.linkUserId";

    private const string CookiePath = "/api/v1/auth/oidc";
    private const string Purpose = "Maki.OidcLinkIntent";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static void Issue(HttpContext context, int userId)
    {
        var token = Protector(context).Protect(userId.ToString(CultureInfo.InvariantCulture), Lifetime);
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = CookiePath,
            MaxAge = Lifetime
        });
    }

    public static bool IsValidFor(HttpContext context, int userId)
    {
        var token = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            return Protector(context).Unprotect(token) == userId.ToString(CultureInfo.InvariantCulture);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Expired, tampered with, or minted under a key ring that no longer exists.
            return false;
        }
    }

    public static void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath });

    private static ITimeLimitedDataProtector Protector(HttpContext context) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .ToTimeLimitedDataProtector();
}
