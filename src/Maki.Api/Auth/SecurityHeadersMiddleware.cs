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
    /// Everything but <c>connect-src</c>, which is completed per request — see
    /// <see cref="PolicyFor"/>.
    /// <para>
    /// <c>style-src</c> allows inline styles because Mantine injects them at runtime; there is no
    /// nonce hook to thread through a third-party CSS-in-JS layer. <c>script-src</c> stays strict,
    /// which is the half that actually stops injected code from running. <c>img-src</c> allows any
    /// <c>https:</c> host because Discover/recommendation posters are served straight from whichever
    /// CDN MangaBaka's source recorded (AniList, anime-planet, MangaBaka's own image proxy, ...) — an
    /// open-ended set that can't be allowlisted per host the way the per-source cover proxy in
    /// <c>SearchController</c> is.
    /// </para>
    /// </summary>
    private const string StaticDirectives =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https:; " +
        "font-src 'self' data:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>
    /// Names this instance's own host in <c>connect-src</c> for the SignalR WebSocket, rather than
    /// the bare <c>ws: wss:</c> schemes.
    /// <para>
    /// A bare scheme matches <b>any</b> host, so <c>connect-src 'self' ws: wss:</c> left the one
    /// directive that limits where a script may send data allowing every WebSocket endpoint on the
    /// internet — which is exactly the channel injected code would exfiltrate over. Do not put them
    /// back.
    /// </para>
    /// <para>
    /// <c>'self'</c> alone is enough on paper: CSP Level 3 has it cover the WebSocket upgrade of the
    /// page's own origin. WebKit did not implement that until Safari 15.4, though, and the failure it
    /// produces is realtime updates silently never arriving — which nobody would report as a CSP
    /// problem. Naming the host explicitly costs two string concatenations and works everywhere.
    /// </para>
    /// <para>
    /// Both schemes are listed because the page may be served over either; the <c>Host</c> header is
    /// client-supplied, but a forged one only ever widens the policy of the attacker's own response.
    /// </para>
    /// </summary>
    private static string PolicyFor(HttpContext context)
    {
        var host = context.Request.Host.Value;
        return string.IsNullOrEmpty(host)
            ? $"{StaticDirectives}; connect-src 'self'"
            : $"{StaticDirectives}; connect-src 'self' ws://{host} wss://{host}";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";

        // no-referrer rather than same-origin: the OPDS catalogue carries its token in the path, so
        // any Referer leaving the origin from an OPDS-rendered page would carry the token with it.
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = PolicyFor(context);

        await next(context);
    }
}
