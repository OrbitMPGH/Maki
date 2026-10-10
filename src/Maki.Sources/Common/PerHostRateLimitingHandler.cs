using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Maki.Sources.Common;

/// <summary>
/// A <see cref="RateLimiter"/> per request host, all built the same way. One bucket shared by
/// clients that talk to unrelated sites makes a walk of one site's chapter pages queue the search
/// and downloads of every other site behind it, which the per-site budget exists to prevent.
/// </summary>
public sealed class HostRateLimiters(Func<RateLimiter> create) : IDisposable
{
    private readonly ConcurrentDictionary<string, RateLimiter> _byHost = new(StringComparer.OrdinalIgnoreCase);

    public RateLimiter For(string? host) => _byHost.GetOrAdd(host ?? string.Empty, _ => create());

    public void Dispose()
    {
        foreach (var limiter in _byHost.Values)
        {
            limiter.Dispose();
        }
    }
}

/// <summary>Delays each request through the limiter of its own host, see <see cref="HostRateLimiters"/>.</summary>
public sealed class PerHostRateLimitingHandler(HostRateLimiters limiters) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await limiters.For(request.RequestUri?.Host).AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException($"Rate limit queue exhausted for {request.RequestUri?.Host}");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
