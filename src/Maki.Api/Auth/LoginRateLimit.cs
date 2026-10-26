using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Maki.Api.Auth;

/// <summary>
/// Applied to sign-in, two-factor, first-run setup, single sign-on and the account actions that
/// confirm a password: at most <see cref="LoginRateLimiter.PermitLimit"/> attempts a minute per
/// client address, so a flood against one account cannot deny sign-in to everyone else. Only
/// meaningful behind a proxy once <c>auth.trustedproxies</c> is set; until then every request looks
/// like it comes from the proxy itself.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class LoginRateLimitAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<LoginRateLimiter>();
}

/// <summary>
/// A fixed window per address that keeps no timer. ASP.NET's rate-limiting middleware did this
/// before, and its partitioned limiter runs a 100 ms heartbeat for the life of the process, the
/// largest source of wakeups left on an idle instance. A window is checked when a request arrives,
/// and expired ones are swept at most once a window, by a request. A rejected attempt is answered
/// 429 at once rather than queued, so waiting never buys a caller a free retry.
/// </summary>
public sealed class LoginRateLimiter(TimeProvider time) : IAsyncResourceFilter
{
    public const int PermitLimit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Bucket> _byAddress = new();
    private long _lastSweep;

    internal int TrackedAddresses => _byAddress.Count;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var address = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (TryAcquire(address, out var retryAfter))
        {
            await next();
            return;
        }

        context.HttpContext.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
    }

    internal bool TryAcquire(string address, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow().UtcTicks;
        Sweep(now);
        while (true)
        {
            var bucket = _byAddress.GetOrAdd(address, _ => new Bucket());
            lock (bucket)
            {
                if (bucket.Removed)
                {
                    continue; // swept between the lookup and the lock; take the fresh one
                }

                if (now - bucket.Start >= Window.Ticks)
                {
                    bucket.Start = now;
                    bucket.Count = 0;
                }

                if (bucket.Count >= PermitLimit)
                {
                    retryAfter = TimeSpan.FromTicks(bucket.Start + Window.Ticks - now);
                    return false;
                }

                bucket.Count++;
                retryAfter = TimeSpan.Zero;
                return true;
            }
        }
    }

    private void Sweep(long now)
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (now - last < Window.Ticks || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        foreach (var (address, bucket) in _byAddress)
        {
            lock (bucket)
            {
                if (now - bucket.Start >= Window.Ticks)
                {
                    bucket.Removed = true;
                    _byAddress.TryRemove(new KeyValuePair<string, Bucket>(address, bucket));
                }
            }
        }
    }

    private sealed class Bucket
    {
        public long Start;
        public int Count;
        public bool Removed;
    }
}
