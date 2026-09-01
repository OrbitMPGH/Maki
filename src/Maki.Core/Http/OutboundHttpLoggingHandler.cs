using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Http;

/// <summary>
/// One log line per outbound call, in place of the four the framework writes.
/// <para>
/// <c>Microsoft.Extensions.Http</c> logs "Start processing", "Sending", "Received response headers"
/// and "End processing" for every request, all at Information, and dumps a full stack trace at
/// Information when a connection is refused. On a library scan or a chapter download that is the
/// bulk of the log, and none of it says anything the single line below does not. The framework
/// categories are floored at Warning in <c>MakiLogging</c>; this replaces them.
/// </para>
/// </summary>
/// <remarks>
/// Installed on every named client at once, as the innermost handler, so what it reports is the
/// call that actually went to the network: after the rate limiter has released it, and once per
/// attempt rather than once per logical request, which is what makes a retry storm visible.
/// </remarks>
public class OutboundHttpLoggingHandler : DelegatingHandler
{
    /// <summary>
    /// An explicit category rather than <c>ILogger&lt;OutboundHttpLoggingHandler&gt;</c>. The
    /// category is what labels the line, this is the highest-volume line in the file at Debug, and
    /// the class name spends twenty-seven columns saying what "OutboundHttp" says.
    /// </summary>
    private readonly ILogger logger;

    public OutboundHttpLoggingHandler(ILoggerFactory loggerFactory)
        => logger = loggerFactory.CreateLogger("Maki.Core.Http.OutboundHttp");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        // The host, not the full URI. Paths carry series ids, chapter slugs and, for a few sources,
        // a signed query string; the log is written to disk and kept for days. Host plus status plus
        // duration is what a "why is this source slow" question needs, and it is all it needs.
        var host = request.RequestUri?.Host ?? "unknown";

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Method} {Host} responded {Status} in {Elapsed:0} ms",
                    request.Method.Method, host, (int)response.StatusCode, elapsed);
            }
            else
            {
                logger.LogDebug("{Method} {Host} responded {Status} in {Elapsed:0} ms",
                    request.Method.Method, host, (int)response.StatusCode, elapsed);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up, most often a shutdown. Not a fault of the call and not worth a line.
            throw;
        }
        catch (Exception ex)
        {
            // Message only, no stack trace. The interesting part of a refused connection or a DNS
            // failure is which host and what it said; the frames below SendAsync are always the same
            // dozen lines of SocketsHttpHandler and are what made these unreadable before.
            logger.LogWarning("{Method} {Host} failed after {Elapsed:0} ms: {Error}",
                request.Method.Method, host, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            throw;
        }
    }
}
