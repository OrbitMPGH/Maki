namespace Maki.Core.Http.Proxy;

/// <summary>
/// How one operation for a source reaches the internet: directly (the process default, which still
/// honours <c>HTTPS_PROXY</c>), or through the proxy the admin assigned to that source.
/// <para>
/// Resolved once at the start of an operation and carried on every request it makes, redirect hops
/// included (<see cref="Option"/>). Cloudflare clearance is bound to the egress IP, so a cookie
/// earned through one route must never leave through another; resolving again mid-operation could
/// do exactly that after a settings save.
/// </para>
/// </summary>
public sealed class ProxyRoute : IEquatable<ProxyRoute>
{
    public static readonly ProxyRoute Direct = new(null);

    /// <summary>The request option <c>ProxyRoutingHandler</c> dispatches on; absent means <see cref="Direct"/>.</summary>
    public static readonly HttpRequestOptionsKey<ProxyRoute> Option = new("maki.proxy-route");

    private ProxyRoute(Uri? proxy)
    {
        Proxy = proxy;
        Fingerprint = proxy is null ? "direct" : proxy.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    public static ProxyRoute Via(Uri proxy) => new(proxy);

    /// <summary>The proxy, or null for <see cref="Direct"/>.</summary>
    public Uri? Proxy { get; }

    /// <summary>Identifies the route in caches (clearance sessions, inner handlers, browser contexts).</summary>
    public string Fingerprint { get; }

    public bool IsDirect => Proxy is null;

    /// <summary>The route a request carries, or <see cref="Direct"/> when it carries none.</summary>
    public static ProxyRoute Of(HttpRequestMessage request) =>
        request.Options.TryGetValue(Option, out var route) ? route : Direct;

    public void ApplyTo(HttpRequestMessage request) => request.Options.Set(Option, this);

    public bool Equals(ProxyRoute? other) => other is not null && Fingerprint == other.Fingerprint;

    public override bool Equals(object? obj) => Equals(obj as ProxyRoute);

    public override int GetHashCode() => Fingerprint.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Fingerprint;
}
