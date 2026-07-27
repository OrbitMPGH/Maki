using Maki.Api.Configuration;

namespace Maki.Api.Middleware;

/// <summary>
/// Requires a valid API key (X-Api-Key header or ?apikey= query) for /api/* and /signalr/*.
/// Static SPA assets and /initialize.json stay open, matching *arr behavior with
/// authentication disabled.
/// </summary>
public class ApiKeyMiddleware(RequestDelegate next, ConfigFileProvider configFile)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        // Cover images stay open so plain <img> tags work in the UI. The scrobble
        // OAuth callback is open because the provider redirects the user's browser
        // there without an API key — it is authenticated by the random OAuth state
        // bound to the in-flight session instead. The OPDS catalogue carries its own
        // token in the path (reading apps take a feed URL and nothing else) and
        // OpdsController checks it on every action — this is a handover, not a hole.
        if ((path.StartsWithSegments("/api") || path.StartsWithSegments("/signalr")) &&
            !path.StartsWithSegments("/api/v1/mediacover") &&
            !path.StartsWithSegments("/api/v1/opds") &&
            !path.StartsWithSegments("/api/v1/scrobble/oauth"))
        {
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                           ?? context.Request.Query["apikey"].FirstOrDefault();

            if (!string.Equals(provided, configFile.Config.ApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
        }

        await next(context);
    }
}
