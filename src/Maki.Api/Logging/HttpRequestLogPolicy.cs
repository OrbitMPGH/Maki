using Serilog.Events;

namespace Maki.Api.Logging;

/// <summary>
/// Decides what level a finished request is logged at.
/// <para>
/// One Information line per request is unusable here. The SPA polls a handful of endpoints on a
/// timer, a library page pulls one cover per visible poster, and a reading session pulls one image
/// per page turn plus a thumbnail strip, so the interesting lines end up buried under traffic that
/// only says the app is running. The tiers below key off what a request <em>did</em> rather than
/// what it asked for: a write, a failure, or an unusually slow call is worth a line, and a
/// successful read is not.
/// </para>
/// <para>
/// Path matching is used only to push known-bulk reads below Debug, so that raising the level to
/// Debug for an afternoon still yields a readable file. It is deliberately a short list of stable
/// prefixes and not an attempt to enumerate the polling endpoints, which change with the frontend
/// and are already covered by the read/write split.
/// </para>
/// </summary>
public static class HttpRequestLogPolicy
{
    /// <summary>
    /// The OPDS catalogue carries its authentication token in the <em>path</em>, and request
    /// logging writes paths. Dropping these below every configured level is what keeps the log
    /// directory from becoming credential material; <c>OpdsController</c> logs its own redacted
    /// line for the case worth debugging. Do not fold this into the tiers below.
    /// </summary>
    private const string OpdsPrefix = "/api/v1/opds";

    /// <summary>
    /// Successful reads whose volume tracks the number of images on screen rather than anything the
    /// user did. Everything not under <c>/api</c> is already covered by <see cref="IsBulkRead"/>:
    /// that is the built SPA shell, its assets, and the hub connection under <c>/signalr</c>.
    /// </summary>
    private static readonly string[] BulkReadPrefixes =
    [
        "/api/v1/mediacover"
    ];

    public static Func<HttpContext, double, Exception?, LogEventLevel> For(LoggingOptions options)
        => (context, elapsedMs, exception) => Resolve(context, elapsedMs, exception, options);

    private static LogEventLevel Resolve(
        HttpContext context,
        double elapsedMs,
        Exception? exception,
        LoggingOptions options)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments(OpdsPrefix))
            return LogEventLevel.Verbose;

        var status = context.Response.StatusCode;

        // An unhandled exception and a 5xx are the same event seen from two places: the middleware
        // catches the throw and turns it into the status, so a request that threw may arrive here
        // with either set. Both are errors regardless of the configured mode.
        if (exception is not null || status >= 500)
            return LogEventLevel.Error;

        if (options.HttpRequests == HttpRequestLogMode.Full)
            return LogEventLevel.Information;

        if (status >= 400)
        {
            // 401, 403 and 404 are ordinary traffic, not faults. A signed-out browser produces a row
            // of 401s on its way to the login page, and any missing favicon or bookmarked URL is a
            // 404; logging those as warnings trains the reader to ignore warnings. The rest (400,
            // 409, 422, 429) mean a client sent something the server refused, which is worth seeing.
            //
            // The ordinary ones take the same demotion a successful read would: a series with no
            // poster answers 404 once per visible tile, so treating a bulk read's 404 as more
            // interesting than its 200 gets the wall of lines back at Debug.
            if (status is 401 or 403 or 404)
                return IsBulkRead(path) ? LogEventLevel.Verbose : LogEventLevel.Debug;

            return LogEventLevel.Warning;
        }

        // Checked before the read/write split so a slow read is not silently demoted to Debug. This
        // is the only tier that can fire on an otherwise entirely successful request.
        if (elapsedMs >= options.SlowRequestMs)
            return LogEventLevel.Warning;

        // Off keeps the failure and slow tiers above and drops everything else, including the
        // record of who wrote what. It exists for a deployment that only wants the log to contain
        // things that went wrong.
        if (options.HttpRequests == HttpRequestLogMode.Off)
            return LogEventLevel.Verbose;

        if (IsMutation(context.Request.Method))
            return LogEventLevel.Information;

        return IsBulkRead(path) ? LogEventLevel.Verbose : LogEventLevel.Debug;
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);

    private static bool IsBulkRead(PathString path)
    {
        if (!path.StartsWithSegments("/api"))
            return true;

        foreach (var prefix in BulkReadPrefixes)
        {
            if (path.StartsWithSegments(prefix))
                return true;
        }

        // Reader page and thumbnail images, /api/v1/reader/chapter/{id}/page/{n} and .../thumb/{n}.
        // Matched on the trailing segment rather than a prefix because the chapter id sits in the
        // middle; the sibling reads under the same controller (bookmarks, progress, continue) are
        // per-navigation and stay at Debug.
        var value = path.Value;
        if (value is not null && path.StartsWithSegments("/api/v1/reader/chapter"))
            return value.Contains("/page/", StringComparison.Ordinal) || value.Contains("/thumb/", StringComparison.Ordinal);

        return false;
    }
}
