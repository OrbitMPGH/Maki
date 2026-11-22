using Maki.Core.Http.Proxy;
using Microsoft.Extensions.Http;

namespace Maki.Api.Http;

/// <summary>
/// Puts a source-facing client on <see cref="ProxyRoutingHandler"/>, so requests carrying a
/// <see cref="ProxyRoute"/> go through the source's proxy. Clients that are not about a source
/// (metadata, trackers, torrent clients, installers, FlareSolverr, Suwayomi) never get it.
/// <c>SourceProxyClientInventoryTests</c> fails for a client nobody classified.
/// </summary>
public static class SourceProxyHttpClientExtensions
{
    /// <summary>
    /// Routes by the route each request carries; callers tag requests themselves (shared clients
    /// like <c>pages</c>). <paramref name="build"/> makes the client's own primary handler for a
    /// proxy, or for none; it defaults to a plain handler with decompression.
    /// </summary>
    public static IHttpClientBuilder RoutesByProxy(this IHttpClientBuilder builder, Func<Uri?, HttpMessageHandler>? build = null) =>
        builder.ConfigurePrimaryHttpMessageHandler(() => new ProxyRoutingHandler(build ?? ProxyRoutingHandler.DefaultBuild));

    /// <summary>
    /// For a client only one source uses: routes like <see cref="RoutesByProxy"/>, and tags every
    /// request without a route with that source's current one. The tag goes outermost, so the
    /// guard and rate limit handlers below it see the route.
    /// </summary>
    public static IHttpClientBuilder ForSource(this IHttpClientBuilder builder, string sourceName, Func<Uri?, HttpMessageHandler>? build = null)
    {
        builder.RoutesByProxy(build);
        builder.Services.Configure<HttpClientFactoryOptions>(builder.Name, options =>
            options.HttpMessageHandlerBuilderActions.Add(handlers =>
                handlers.AdditionalHandlers.Insert(0, new SourceProxyTagHandler(
                    handlers.Services.GetRequiredService<ISourceProxyResolver>(), sourceName))));
        return builder;
    }
}
