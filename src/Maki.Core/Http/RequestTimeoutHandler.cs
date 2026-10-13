namespace Maki.Core.Http;

/// <summary>
/// Bounds one trip down the rest of the handler chain until the response headers arrive. Placed after
/// a <see cref="RateLimitingHandler"/> it times only the request itself, not the wait for a permit,
/// which <see cref="HttpClient.Timeout"/> cannot tell apart. A timeout surfaces the way the client's
/// own does: a <see cref="TaskCanceledException"/> wrapping a <see cref="TimeoutException"/>.
/// </summary>
public sealed class RequestTimeoutHandler(TimeSpan timeout) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await base.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"The request was canceled due to the configured timeout of {timeout.TotalSeconds:0} seconds elapsing.",
                new TimeoutException());
        }
    }
}
