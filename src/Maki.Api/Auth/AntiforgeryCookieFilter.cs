using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Maki.Api.Auth;

/// <summary>
/// CSRF protection for state-changing requests, applied only to the requests that actually have a
/// CSRF surface.
/// <para>
/// A cookie session is sent by the browser on any request the attacker's page can provoke, so every
/// mutating endpoint needs a second factor the attacker cannot read cross-origin. <c>SameSite=Lax</c>
/// on the session cookie already blocks cross-site POST/PUT/DELETE; this is the defence in depth
/// behind it, and it is what still holds if the cookie policy is ever loosened.
/// </para>
/// <para>
/// Requests authenticated by an API key are skipped deliberately: a header credential is never sent
/// automatically by a browser, so there is nothing to forge — and requiring a token there would break
/// every script and third-party client for no security gain.
/// </para>
/// </summary>
public sealed class AntiforgeryCookieFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;

        if (SafeMethods.Contains(request.Method))
        {
            return;
        }

        if (context.ActionDescriptor.EndpointMetadata.OfType<IAntiforgeryPolicy>()
            .Any(m => m is IgnoreAntiforgeryTokenAttribute))
        {
            return;
        }

        // Only the cookie scheme is browser-ambient. IdentityConstants.ApplicationScheme is the
        // AuthenticationType the Identity claims factory stamps on a cookie-signed principal.
        if (context.HttpContext.User.Identity?.AuthenticationType != IdentityConstants.ApplicationScheme)
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(new { error = "Invalid or missing antiforgery token" });
        }
    }
}

/// <summary>
/// Publishes the antiforgery request token in a JavaScript-readable cookie so the SPA can echo it
/// back in the <c>X-XSRF-TOKEN</c> header.
/// <para>
/// This cookie is <em>not</em> the secret — the antiforgery system's own companion cookie is, and it
/// stays <c>HttpOnly</c>. Same-origin policy is what stops an attacker's page from reading this one,
/// which is the whole basis of the double-submit pattern.
/// </para>
/// </summary>
public class AntiforgeryTokenMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    public const string CookieName = "XSRF-TOKEN";

    public async Task InvokeAsync(HttpContext context)
    {
        // Reissued on *every* GET, not only when the cookie is absent.
        //
        // An antiforgery token is bound to the identity it was issued to, so the one handed to an
        // anonymous visitor stops validating the moment they sign in — and a "only if missing" check
        // would keep serving that dead token, making every mutation after login fail with "invalid or
        // missing antiforgery token". Reissuing on each GET means the first read after any identity
        // change rebinds it. It costs an HMAC and a cookie header, which is nothing next to the
        // database work a typical GET here already does.
        if (HttpMethods.IsGet(context.Request.Method))
        {
            IssueToken(context, antiforgery);
        }

        await next(context);
    }

    /// <summary>
    /// Writes a freshly bound request token to the readable cookie. Also called straight after
    /// sign-in, where waiting for the next GET would leave a window in which a mutation fails once.
    /// </summary>
    public static void IssueToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        if (tokens.RequestToken is null)
        {
            return;
        }

        context.Response.Cookies.Append(CookieName, tokens.RequestToken, new CookieOptions
        {
            // Readable by design — this is the half the SPA echoes back in a header. The secret half
            // is the antiforgery system's own companion cookie, which stays HttpOnly.
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/"
        });
    }
}
