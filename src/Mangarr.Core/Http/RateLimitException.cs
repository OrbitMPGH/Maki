namespace Mangarr.Core.Http;

/// <summary>
/// Raised when a source responds with HTTP 429 (Too Many Requests) or 503
/// (Service Unavailable), carrying the server's Retry-After delay when it sent one.
/// The download pipeline treats this specially: instead of failing the chapter it
/// backs the whole scraper queue off for a cooldown and retries later.
/// </summary>
public class RateLimitException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    /// <summary>The server-requested wait, if a Retry-After header was present.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
