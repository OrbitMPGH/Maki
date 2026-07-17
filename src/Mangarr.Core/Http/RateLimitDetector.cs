using System.Net;

namespace Mangarr.Core.Http;

/// <summary>
/// Recognises a rate-limit signal anywhere in an exception chain, so every caller agrees on what
/// "the source is throttling us" looks like regardless of which layer raised it.
/// </summary>
public static class RateLimitDetector
{
    /// <summary>
    /// True when <paramref name="ex"/> (or anything it wraps) is a rate limit: our own
    /// <see cref="RateLimitException"/>, which carries Retry-After, or a bare
    /// <see cref="HttpRequestException"/> with a 429/503 status from a source's own call.
    /// </summary>
    public static bool IsRateLimit(Exception ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        switch (ex)
        {
            case RateLimitException rle:
                retryAfter = rle.RetryAfter;
                return true;
            case HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }:
                return true;
            case AggregateException agg:
                foreach (var aggregated in agg.InnerExceptions)
                {
                    if (IsRateLimit(aggregated, out retryAfter))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return ex.InnerException is { } inner && IsRateLimit(inner, out retryAfter);
        }
    }
}
