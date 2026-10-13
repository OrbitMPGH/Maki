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
/// <para>
/// A <see cref="BadHttpRequestException"/> is the client's doing (an oversized body, a malformed
/// request line) and keeps the status Kestrel chose, with an empty body and no stack trace.
/// </para>
/// </summary>
public sealed class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public const string Key = "error.server.unexpected";

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var path = context.Request.Path.StartsWithSegments(HttpRequestLogPolicy.OpdsPrefix)
            ? HttpRequestLogPolicy.OpdsPrefix
            : context.Request.Path.Value;

        if (exception is BadHttpRequestException bad)
        {
            logger.LogInformation("Rejected {Method} {Path} with {Status}: {Reason}",
                context.Request.Method, path, bad.StatusCode, bad.Message);
            context.Response.StatusCode = bad.StatusCode;
            return true;
        }

        logger.LogError(exception, "Unhandled exception in {Method} {Path}", context.Request.Method, path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { code = Key, error = Message(context) }, ct);
        return true;
    }

    // Resolving the locale can read the database, which is the likeliest thing to be why we are here.
    // The key itself is what ILocalizer answers for an unknown message, so it is the fallback too.
    private static string Message(HttpContext context)
    {
        try
        {
            return context.RequestServices.GetRequiredService<ILocalizer>().Get(Key);
        }
        catch (Exception)
        {
            return Key;
        }
    }
}
