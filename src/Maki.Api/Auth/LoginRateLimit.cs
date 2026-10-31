using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Maki.Api.Auth;

/// <summary>
/// Applied to sign-in, two-factor, first-run setup, single sign-on and the account actions that
/// confirm a password: at most <see cref="LoginRateLimiter.PermitLimit"/> attempts a minute per
/// client address, so a flood against one account cannot deny sign-in to everyone else. Only
/// meaningful behind a proxy once <c>auth.trustedproxies</c> is set; until then every request looks
/// like it comes from the proxy itself. Enforced by <see cref="LoginRateLimitMiddleware"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class LoginRateLimitAttribute : Attribute;

/// <summary>
/// Runs before authentication, as the rate-limiting middleware did, so a refused attempt costs no
/// session or API key lookup and a request the auth pipeline would answer 401 is still counted.
/// </summary>
public sealed class LoginRateLimitMiddleware(RequestDelegate next, LoginRateLimiter limiter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<LoginRateLimitAttribute>() is null
            || limiter.TryAcquire(LoginRateLimiter.KeyFor(context.Connection.RemoteIpAddress), out var retryAfter))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A fixed window per address that keeps no timer. ASP.NET's rate-limiting middleware did this
/// before, and its partitioned limiter runs a 100 ms heartbeat for the life of the process, the
/// largest source of wakeups left on an idle instance. A window is checked when a request arrives,
/// and expired ones are swept at most once a window, by a request. A rejected attempt is answered
/// 429 at once rather than queued, so waiting never buys a caller a free retry.
/// </summary>
public sealed class LoginRateLimiter(TimeProvider time)
{
    public const int PermitLimit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Bucket> _byAddress = new();
    private long _lastSweep;

    internal int TrackedAddresses => _byAddress.Count;

    /// <summary>
    /// The bucket an address spends from. A dual-stack listener reports an IPv4 client as
    /// <c>::ffff:a.b.c.d</c>, which must share the plain form's bucket, and an IPv6 client usually
    /// holds a whole /64 it can rotate through, so IPv6 is limited per /64.
    /// </summary>
    internal static string KeyFor(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address))
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
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
