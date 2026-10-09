namespace Maki.Api.Auth;

/// <summary>
/// Logs once when a request carries <c>X-Forwarded-For</c> that the forwarded-headers middleware did
/// not act on. That is the signature of a reverse proxy missing from <c>auth.trustedproxies</c>, where
/// every client shares the proxy's address and so one rate-limit bucket and one lockout counter.
/// Runs after <c>UseForwardedHeaders</c>, which stamps <c>X-Original-For</c> when it did act.
/// </summary>
public class UntrustedForwardedForWarning(RequestDelegate next, ILogger<UntrustedForwardedForWarning> logger)
{
    private int _warned;

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("X-Forwarded-For")
            && !context.Request.Headers.ContainsKey("X-Original-For")
            && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "A request arrived with X-Forwarded-For from {Remote}, which is not in auth.trustedproxies. "
                + "If that is your reverse proxy, add it to Trusted proxies in the security settings, or every client will "
                + "share one sign-in rate limit and lockout counter",
                context.Connection.RemoteIpAddress);
        }

        return next(context);
    }
}
