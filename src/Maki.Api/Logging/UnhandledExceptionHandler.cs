using Maki.Api.Auth;
using Maki.Api.Localization;
using Microsoft.AspNetCore.Diagnostics;

namespace Maki.Api.Logging;

/// <summary>
/// Turns an exception nothing else caught into one log entry and the same <c>{ code, error }</c>
/// body the controllers answer failures with.
/// <para>
/// Without it the exception propagates through request logging, which records it, and on to Kestrel,
/// which records it again, so every 500 wrote two stack traces. Handling it here means neither sees
/// it. The OPDS catalogue carries its token in the path, so that prefix is logged without the rest.
/// </para>
/// </summary>
public sealed class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public const string Key = "error.server.unexpected";

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return true;
        }

        var path = context.Request.Path.StartsWithSegments(HttpRequestLogPolicy.OpdsPrefix)
            ? HttpRequestLogPolicy.OpdsPrefix
            : context.Request.Path.Value;
        logger.LogError(exception, "Unhandled exception in {Method} {Path}", context.Request.Method, path);

        SecurityHeadersMiddleware.Apply(context);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var localizer = context.RequestServices.GetRequiredService<ILocalizer>();
        await context.Response.WriteAsJsonAsync(new { code = Key, error = localizer.Get(Key) }, ct);
        return true;
    }
}
