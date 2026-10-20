using System.Security.Cryptography;
using System.Text;
using Maki.Api.Auth;
using Maki.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Komga;

/// <summary>
/// Resolves a Komga client's credential to the OPDS token it stands for.
/// <para>
/// Three ways in, all carrying the same <c>UserApiKeyScope.Opds</c> token: the <c>X-API-Key</c>
/// header (Komga's own API keys), HTTP Basic with the token as the password (clients that only ask
/// for a username and password), and the session cookie issued here after either succeeds. The
/// cookie is not optional: the Mihon Komga tracker shares the source's cookie jar and sends no
/// credential of its own, and the source itself only adds Basic after a 401.
/// </para>
/// <para>
/// The cookie holds the token under data protection, so revoking or rotating the token, switching
/// either catalogue off or disabling the owner all apply to it at once. It never authenticates a
/// POST: that is the one unsafe method a page on the same site can send cross-origin without a
/// preflight, and Komga clients authenticate their requests with a header anyway.
/// </para>
/// </summary>
public class KomgaAuthenticator(OpdsAccessService access, IDataProtectionProvider protection)
{
    public const string CookieName = "Maki.Komga";
    public const string ApiKeyHeader = "X-API-Key";

    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    private ITimeLimitedDataProtector Protector =>
        protection.CreateProtector("Maki.Komga.Session").ToTimeLimitedDataProtector();

    /// <returns>Null when no credential resolves to a user with the Komga API switched on.</returns>
    public async Task<OpdsAccess?> AuthenticateAsync(HttpContext http, CancellationToken ct)
    {
        var request = http.Request;
        var fromCookie = request.Cookies.TryGetValue(CookieName, out var cookie) ? Unprotect(cookie) : null;
        var presented = HeaderToken(request);
        var token = presented ?? (HttpMethods.IsPost(request.Method) ? null : fromCookie);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (await access.ResolveAsync(token, ct) is not { Komga: true } resolved)
        {
            return null;
        }

        if (presented is not null && presented != fromCookie)
        {
            http.Response.Cookies.Append(CookieName, Protector.Protect(presented, CookieLifetime), new CookieOptions
            {
                HttpOnly = true,
                // Lax, as Komga's own session cookie is: Android's cookie store is what the Mihon
                // tracker reads from, and it is not a browser context that Strict was designed for.
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Secure = AuthRuntimeOptions.UseSecureCookie(http),
                Path = $"{request.PathBase}/komga",
                MaxAge = CookieLifetime,
            });
        }

        return resolved;
    }

    private static string? HeaderToken(HttpRequest request)
    {
        if (request.Headers[ApiKeyHeader].FirstOrDefault() is { Length: > 0 } key)
        {
            return key;
        }

        var authorization = request.Headers[HeaderNames.Authorization].FirstOrDefault();
        const string basic = "Basic ";
        if (authorization is null || !authorization.StartsWith(basic, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[basic.Length..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon >= 0 && colon < decoded.Length - 1 ? decoded[(colon + 1)..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string? Unprotect(string value)
    {
        try
        {
            return Protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
