using System.Net;
using System.Net.Sockets;

namespace Maki.Core.Http;

/// <summary>
/// Thrown when a page or image URL a source handed back points somewhere other than the public
/// internet. An <see cref="HttpRequestException"/> so callers that already handle a failed page
/// fetch treat it the same way.
/// </summary>
public class BlockedDestinationException(string message) : HttpRequestException(message);

/// <summary>
/// Keeps server-side fetches of site-supplied page and image URLs off the host's own network
/// (SSRF). Only for page/image clients: FlareSolverr, Kavita, qBittorrent, Prowlarr and the
/// metadata clients legitimately talk to the LAN and must not get this handler.
/// <para>
/// <see cref="EnsureAllowed(string)"/> is the cheap pre-check on the URL itself (scheme, literal
/// IPs, localhost). <see cref="CreateHandler"/> is the real enforcement: it checks every resolved
/// address at connect time, which covers redirects and DNS rebinding.
/// </para>
/// </summary>
public static class PublicAddressGuard
{
    public static void EnsureAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new BlockedDestinationException($"Refusing to fetch non-absolute URL '{url}'");
        }

        EnsureAllowed(uri);
    }

    public static void EnsureAllowed(Uri uri)
    {
        if (!IsAllowedUrl(uri))
        {
            throw new BlockedDestinationException($"Refusing to fetch '{uri}': not a public http(s) address");
        }
    }

    public static bool IsAllowedUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out var literal) || IsPublic(literal);
    }

    /// <summary>
    /// Resolves the URL's host and requires every address to be public. For fetchers that can't take
    /// <see cref="CreateHandler"/> (a headless browser), where a connect-time check isn't possible.
    /// </summary>
    public static async Task<bool> IsPublicHostAsync(Uri uri, CancellationToken ct)
    {
        if (!IsAllowedUrl(uri))
        {
            return false;
        }

        if (IPAddress.TryParse(uri.IdnHost, out _))
        {
            return true;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, ct);
            return addresses.Length > 0 && addresses.All(IsPublic);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsPublicV4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
            address.IsIPv6UniqueLocal)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        // NAT64 (64:ff9b::/96) and IPv4-compatible (::/96) addresses embed an IPv4 address in the low 32 bits.
        var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
        var v4Compatible = b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0;
        if (nat64 || v4Compatible)
        {
            return IsPublicV4(b[12..]);
        }

        // 6to4 (2002::/16) embeds an IPv4 address in bytes 2-5; unwrap and check that instead.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicV4(b[2..6]);
        }

        // Teredo (2001::/32): a tunnel to an arbitrary, unauthenticated IPv4 endpoint carried in the
        // address itself, and local-use NAT64 (64:ff9b:1::/48, distinct from the global 64:ff9b::/96
        // above): neither names a fixed, publicly reachable host, so treat both as non-public.
        var teredo = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00;
        var nat64LocalUse = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01;
        if (teredo || nat64LocalUse)
        {
            return false;
        }

        return true;
    }

    private static bool IsPublicV4(byte[] b) => !(
        b[0] == 0 ||                                  // 0.0.0.0/8, unspecified
        b[0] == 10 ||                                 // 10/8
        b[0] == 127 ||                                // loopback
        (b[0] == 100 && (b[1] & 0xC0) == 64) ||       // 100.64/10, CGNAT
        (b[0] == 169 && b[1] == 254) ||               // link-local
        (b[0] == 172 && (b[1] & 0xF0) == 16) ||       // 172.16/12
        (b[0] == 192 && b[1] == 168) ||               // 192.168/16
        (b[0] == 192 && b[1] == 0 && b[2] == 0) ||    // 192.0.0.0/24, IETF protocol assignments
        (b[0] == 198 && (b[1] & 0xFE) == 18) ||       // 198.18/15, benchmarking
        b[0] >= 224);                                 // multicast, reserved, broadcast

    /// <summary>
    /// A primary handler whose connections may only reach public addresses. A connection to the
    /// configured HTTP proxy is let through, since the proxy (not this process) makes the real one.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new() { ConnectCallback = ConnectAsync };

    /// <summary>
    /// <see cref="CreateHandler"/> with automatic redirects off, for clients whose
    /// <see cref="ProxiedTargetGuardHandler"/> follows and re-checks them.
    /// </summary>
    public static SocketsHttpHandler CreateManualRedirectHandler()
    {
        var handler = CreateHandler();
        handler.AllowAutoRedirect = false;
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);

        if (IsConfiguredProxy(context))
        {
            // The connect-time addresses above are the proxy's, not the real destination's, so they
            // tell us nothing here. The proxy makes the real connection, but redirects and hostnames
            // resolving to a private address are invisible to us once traffic goes through it, so
            // check the target URL itself before letting the request through.
            await EnsureTargetPublicAsync(context.InitialRequestMessage.RequestUri, ct);
        }
        else
        {
            addresses = addresses.Where(IsPublic).ToArray();
            if (addresses.Length == 0)
            {
                throw new BlockedDestinationException(
                    $"Refusing to connect to {endpoint.Host}: it does not resolve to a public address");
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The destination of a plain-http request that goes through a proxy, or null when it does not.</summary>
    internal static Uri? ProxiedPlainHttpTarget(HttpRequestMessage request, IWebProxy proxy)
    {
        var target = request.RequestUri;
        if (target is null || target.Scheme != Uri.UriSchemeHttp || proxy.IsBypassed(target))
        {
            return null;
        }

        return proxy.GetProxy(target) is { } via && via != target ? target : null;
    }

    internal static async ValueTask EnsureTargetPublicAsync(Uri? target, CancellationToken ct)
    {
        if (target is null || !IsAllowedUrl(target))
        {
            throw new BlockedDestinationException(
                $"Refusing to proxy to '{target}': not a public http(s) address");
        }

        if (IPAddress.TryParse(target.IdnHost, out var literal))
        {
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(target.IdnHost, ct);
        }
        catch (SocketException)
        {
            throw new BlockedDestinationException($"Refusing to proxy to '{target}': host does not resolve");
        }

        if (addresses.Length == 0 || !addresses.All(IsPublic))
        {
            throw new BlockedDestinationException(
                $"Refusing to proxy to '{target}': it does not resolve to a public address");
        }
    }

    private static bool IsConfiguredProxy(SocketsHttpConnectionContext context)
    {
        var target = context.InitialRequestMessage.RequestUri;
        if (target == null)
        {
            return false;
        }

        var proxy = HttpClient.DefaultProxy.GetProxy(target);
        return proxy != null && proxy != target &&
               proxy.Port == context.DnsEndPoint.Port &&
               proxy.IdnHost.Equals(context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Checks the target of every plain-http request that goes through a proxy. Such requests share one
/// pooled connection to the proxy whatever their destination, so <see cref="PublicAddressGuard.CreateHandler"/>
/// only sees the first one. Add it after the guarded primary handler on every client that uses it.
/// <para>
/// With <paramref name="followRedirects"/> the handler follows up to five redirects itself and checks
/// each hop, which needs the primary handler built by
/// <see cref="PublicAddressGuard.CreateManualRedirectHandler"/>. A redirect that
/// <c>SocketsHttpHandler</c> follows on its own would reuse the proxy connection unchecked.
/// </para>
/// </summary>
public sealed class ProxiedTargetGuardHandler(IWebProxy? proxy = null, bool followRedirects = false) : DelegatingHandler
{
    private const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            await CheckAsync(request, ct);
            var response = await base.SendAsync(request, ct);
            if (!followRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            var keepsMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            var downgrade = request.RequestUri!.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp;
            if (downgrade || (keepsMethod && request.Content is not null))
            {
                return response;
            }

            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException($"Too many redirects from '{request.RequestUri}'");
            }

            response.Dispose();
            PublicAddressGuard.EnsureAllowed(next);
            request = Follow(request, next, keepsMethod);
        }
    }

    private async ValueTask CheckAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (PublicAddressGuard.ProxiedPlainHttpTarget(request, proxy ?? HttpClient.DefaultProxy) is { } target)
        {
            await PublicAddressGuard.EnsureTargetPublicAsync(target, ct);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage Follow(HttpRequestMessage previous, Uri next, bool keepsMethod)
    {
        var method = keepsMethod || previous.Method == HttpMethod.Head ? previous.Method : HttpMethod.Get;
        var request = new HttpRequestMessage(method, next) { Version = previous.Version, VersionPolicy = previous.VersionPolicy };
        var sameHost = string.Equals(previous.RequestUri?.IdnHost, next.IdnHost, StringComparison.OrdinalIgnoreCase);
        foreach (var header in previous.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                (!sameHost && header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }
}
