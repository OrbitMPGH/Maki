using System.Threading.RateLimiting;

namespace Maki.Core.Http;

/// <summary>
/// Delays outgoing requests through a shared RateLimiter. Attach one instance per
/// named HttpClient so all callers of a host share the same budget.
/// </summary>
public class RateLimitingHandler(RateLimiter limiter) : DelegatingHandler
{
    /// <summary>
    /// Set on a request that must not wait behind bulk traffic, a health probe with a short timeout.
    /// A probe stuck in the queue reads as the service being down.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> Skip = new("Maki.RateLimit.Skip");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(Skip, out var skip) && skip)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        using var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException($"Rate limit queue exhausted for {request.RequestUri?.Host}");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    public static RateLimiter TokenBucket(int tokensPerPeriod, TimeSpan period, int burst = 0)
    {
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(burst, tokensPerPeriod),
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = period,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }
}
