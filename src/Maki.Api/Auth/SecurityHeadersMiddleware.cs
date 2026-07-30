namespace Maki.Api.Auth;

/// <summary>
/// Response hardening headers for the SPA and the API.
/// <para>
/// These matter far more once Maki is reachable from the internet with a cookie session: without a
/// CSP, one reflected script anywhere in the SPA can drive the whole management API as the logged-in
/// admin, and without <c>frame-ancestors</c> the login page can be framed for clickjacking.
/// </para>
/// </summary>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// <c>style-src</c> allows inline styles because Mantine injects them at runtime; there is no
    /// nonce hook to thread through a third-party CSS-in-JS layer. <c>script-src</c> stays strict,
    /// which is the half that actually stops injected code from running.
    /// <c>connect-src</c> includes the websocket schemes for the SignalR hub.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";

        // no-referrer rather than same-origin: the OPDS catalogue carries its token in the path, so
        // any Referer leaving the origin from an OPDS-rendered page would carry the token with it.
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        await next(context);
    }
}
