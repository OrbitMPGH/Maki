using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Maki.Core.Http;

/// <summary>
/// Same idea as <see cref="RateLimitingHandler"/> but keyed per-host. The "pages" HttpClient is
/// shared across every source's image downloads, so a single bucket would throttle unrelated
/// hosts together; this gives each host its own budget, created lazily on first request.
/// </summary>
public class PerHostRateLimitingHandler(Func<string, RateLimiter> limiterFactory) : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "";
        var limiter = _limiters.GetOrAdd(host, limiterFactory);

        using var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException($"Rate limit queue exhausted for {host}");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
