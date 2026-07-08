using Mangarr.Api.Configuration;

namespace Mangarr.Api.Middleware;

/// <summary>
/// Requires a valid API key (X-Api-Key header or ?apikey= query) for /api/* and /signalr/*.
/// Static SPA assets and /initialize.json stay open, matching *arr behavior with
/// authentication disabled.
/// </summary>
public class ApiKeyMiddleware(RequestDelegate next, ConfigFileProvider configFile)
{
    private readonly string _apiKey = configFile.Config.ApiKey;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/signalr"))
        {
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                           ?? context.Request.Query["apikey"].FirstOrDefault();

            if (!string.Equals(provided, _apiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
        }

        await next(context);
    }
}
