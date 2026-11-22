using System.Net;
using System.Net.Sockets;
using System.Text;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Maki.Core.Http.Proxy;
using Maki.Core.Sources;
using Maki.Sources.Common;

namespace Maki.Api.Services;

/// <param name="Status">What the site answered through the proxy; null when the proxy failed.</param>
/// <param name="Failure">Why nothing came back, when it did not.</param>
/// <param name="AllowsPrivate">
/// Whether the proxy agreed to connect to its own loopback address. Null when that could not be
/// asked. A proxy that does would let a site's hostname that resolves privately for the proxy reach
/// the proxy's network.
/// </param>
/// <param name="FlareSolverr">FlareSolverr's own try through the proxy; null when the source does not need it or none is set.</param>
/// <param name="TimedOut">
/// Nothing came back from the site within the test's time limit. That says nothing about whether
/// the proxy is reachable, so it is not reported as <see cref="SourceProxyFailure.Unreachable"/>.
/// </param>
public record SourceProxyTestResult(
    int? Status, SourceProxyFailure? Failure, bool? AllowsPrivate, FlareSolverrProxyResult? FlareSolverr, bool TimedOut = false);

/// <param name="Detail">What FlareSolverr said when it failed, as it said it.</param>
public record FlareSolverrProxyResult(bool Ok, string? Detail);

/// <summary>
/// The Source proxy card's Test: whether the proxy reaches the source's site, whether it refuses
/// private destinations, and whether FlareSolverr can use it too. It reports that the proxy got
/// through, not that the site is healthy: a site behind Cloudflare answers a bare request with 403
/// whether or not the address is blocked.
/// </summary>
public sealed class SourceProxyTester(FlareSolverrClient flareSolverr, IAppSettings settings, ILogger<SourceProxyTester> logger)
{
    private static readonly TimeSpan SiteTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<SourceProxyTestResult> TestAsync(Uri proxy, ISource source, CancellationToken ct)
    {
        var route = ProxyRoute.Via(proxy);
        var (status, failure, timedOut) = await ReachSiteAsync(route, source, ct);
        var allowsPrivate = failure is SourceProxyFailure.Unreachable ? null : await AllowsPrivateAsync(proxy, ct);

        FlareSolverrProxyResult? flare = null;
        var flareUrl = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        if (failure is null && !timedOut && source.Capabilities.HasFlag(SourceCapabilities.NeedsFlareSolverr) && !string.IsNullOrWhiteSpace(flareUrl))
        {
            flare = await TryFlareSolverrAsync(flareUrl, source, route, ct);
        }

        return new SourceProxyTestResult(status, failure, allowsPrivate, flare, timedOut);
    }

    private async Task<(int? Status, SourceProxyFailure? Failure, bool TimedOut)> ReachSiteAsync(ProxyRoute route, ISource source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SiteTimeout);
        using var client = new HttpClient(new ProxyRoutingHandler(PublicAddressGuard.CreateManualRedirectHandler))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, source.BaseUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent.Value);
        route.ApplyTo(request);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return ((int)response.StatusCode, null, false);
        }
        catch (SourceProxyException ex)
        {
            logger.LogInformation("Source proxy test for {Source}: {Message}", source.Name, ex.Message);
            return (null, ex.Failure, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A proxy that is not there fails fast with a connection error; a timeout means something
            // accepted the connection and then the site, or the proxy, went quiet.
            return (null, null, true);
        }
        catch (HttpRequestException ex)
        {
            // Through the proxy but not to the site (TLS, a refused destination): the proxy answered.
            logger.LogInformation("Source proxy test for {Source} reached the proxy but not the site: {Message}", source.Name, ex.Message);
            return (null, SourceProxyFailure.Rejected, false);
        }
    }

    /// <summary>
    /// Asks the proxy for a tunnel to 127.0.0.1:443. A 403, 405 or 407 is a refusal; anything else
    /// means the proxy tried, which is enough to warn about whether or not something answered there.
    /// </summary>
    private static async Task<bool?> AllowsPrivateAsync(Uri proxy, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(proxy.IdnHost, proxy.Port, timeout.Token);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:443 HTTP/1.1\r\nHost: 127.0.0.1:443\r\n\r\n"), timeout.Token);
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            var parts = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0].Split(' ', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
            {
                return null;
            }

            return status is not ((int)HttpStatusCode.Forbidden or (int)HttpStatusCode.MethodNotAllowed
                or (int)HttpStatusCode.ProxyAuthenticationRequired);
        }
        catch (Exception ex) when (ex is SocketException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<FlareSolverrProxyResult> TryFlareSolverrAsync(string flareUrl, ISource source, ProxyRoute route, CancellationToken ct)
    {
        try
        {
            await flareSolverr.GetAsync(flareUrl, source.BaseUrl, route, ct);
            return new FlareSolverrProxyResult(true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogInformation("Source proxy test: FlareSolverr could not go through the proxy for {Source}: {Message}", source.Name, ex.Message);
            return new FlareSolverrProxyResult(false, ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
        }
    }
}
