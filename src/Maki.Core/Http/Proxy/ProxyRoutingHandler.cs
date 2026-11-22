using System.Collections.Concurrent;
using System.Net;

namespace Maki.Core.Http.Proxy;

/// <summary>
/// Primary handler for source-facing clients: sends each request on an inner handler for the
/// <see cref="ProxyRoute"/> it carries, built by the client's own factory with that proxy applied.
/// A request with no route goes <see cref="ProxyRoute.Direct"/>, which is exactly the handler the
/// client had before routing existed.
/// <para>
/// Inner handlers live as long as this one: <c>IHttpClientFactory</c> retires the whole handler on
/// its usual rotation, which is what eventually drops a proxy the admin changed. Nothing is evicted
/// early, so a response stream from an old route stays readable.
/// </para>
/// </summary>
public sealed class ProxyRoutingHandler(Func<Uri?, HttpMessageHandler> build) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Lazy<HttpMessageInvoker>> _routes = new();
    private bool _disposed;

    /// <summary>A plain handler with decompression on, the same as the default every other client gets.</summary>
    public static HttpMessageHandler DefaultBuild(Uri? proxy) => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        Proxy = proxy is null ? null : new WebProxy(proxy),
        UseProxy = true,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var route = ProxyRoute.Of(request);
        var invoker = _routes.GetOrAdd(route.Fingerprint, _ => new Lazy<HttpMessageInvoker>(
            () => new HttpMessageInvoker(build(route.Proxy), disposeHandler: true))).Value;

        if (route.IsDirect)
        {
            return await invoker.SendAsync(request, ct);
        }

        HttpResponseMessage response;
        try
        {
            response = await invoker.SendAsync(request, ct);
        }
        catch (HttpRequestException ex) when (Classify(ex) is { } failure)
        {
            throw new SourceProxyException(null, failure, ex,
                $"Could not go through the proxy {route.Fingerprint} for {request.RequestUri?.Host}: {ex.Message}");
        }

        if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            response.Dispose();
            throw new SourceProxyException(null, SourceProxyFailure.Rejected, null,
                $"The proxy {route.Fingerprint} asked for credentials for {request.RequestUri?.Host}");
        }

        return response;
    }

    /// <summary>
    /// Which proxy failure an exception on a proxied route is, or null for one that is not about the
    /// proxy. A refused destination is the SSRF guard speaking, and stays what it is.
    /// </summary>
    internal static SourceProxyFailure? Classify(HttpRequestException ex)
    {
        if (ex is SourceProxyException)
        {
            return null;
        }

        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is BlockedDestinationException)
            {
                return null;
            }
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError => SourceProxyFailure.Unreachable,
            HttpRequestError.ProxyTunnelError => SourceProxyFailure.Rejected,
            _ => null,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            foreach (var route in _routes.Values)
            {
                if (route.IsValueCreated)
                {
                    route.Value.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Puts a single-source client's <see cref="ProxyRoute"/> on each request that does not carry one
/// yet. Requests made inside a longer operation (a solved session, a chapter's pages) carry the
/// operation's route already and keep it.
/// </summary>
public sealed class SourceProxyTagHandler(ISourceProxyResolver resolver, string sourceName) : DelegatingHandler
{
    public string SourceName { get; } = sourceName;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.Options.TryGetValue(ProxyRoute.Option, out _))
        {
            (await resolver.ResolveAsync(SourceName, ct)).ApplyTo(request);
        }

        return await base.SendAsync(request, ct);
    }
}
