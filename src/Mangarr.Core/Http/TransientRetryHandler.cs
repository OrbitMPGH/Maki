using System.Net;

namespace Mangarr.Core.Http;

/// <summary>
/// Retries transient failures — a dropped connection, a DNS blip, a 5xx from a service that's
/// still starting up — with exponential backoff and jitter. Without it, every external call
/// (MangaBaka, Prowlarr, qBittorrent, Kavita, AniList, MAL) failed permanently on the first
/// hiccup, surfacing as a hard error the user had to retry by hand.
/// </summary>
/// <remarks>
/// Deliberately narrow in two ways:
/// <list type="bullet">
/// <item>
/// Only GET/HEAD are retried. The others aren't safe to repeat blind — a resend can't tell
/// "never arrived" from "arrived, reply lost", and replaying a qBittorrent add or a Kavita scan
/// would duplicate real work.
/// </item>
/// <item>
/// 429 and 503 are left alone. Those mean "you are asking too often", and the answer is the
/// shared cooldown (<see cref="RateLimitException"/> / <see cref="RateLimitingHandler"/>), not a
/// fast retry into the same wall.
/// </item>
/// </list>
/// </remarks>
public class TransientRetryHandler(int maxAttempts = 3, TimeSpan? baseDelay = null) : DelegatingHandler
{
    private readonly TimeSpan _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsRetryable(request.Method))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        for (var attempt = 1; ; attempt++)
        {
            var lastAttempt = attempt >= maxAttempts;
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (lastAttempt || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                response.Dispose();
            }
            catch (Exception ex) when (!lastAttempt && IsTransientException(ex, cancellationToken))
            {
                // Fall through to the delay and try again.
            }

            await Task.Delay(DelayFor(attempt), cancellationToken);
        }
    }

    private static bool IsRetryable(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;

    /// <summary>5xx worth a second try, plus 408. 501/505 are permanent, and 429/503 are rate limits.</summary>
    private static bool IsTransient(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => false,
        HttpStatusCode.NotImplemented or HttpStatusCode.HttpVersionNotSupported => false,
        >= (HttpStatusCode)500 => true,
        _ => false,
    };

    private static bool IsTransientException(Exception ex, CancellationToken ct) => ex switch
    {
        // The caller gave up, or a rate limit was already detected upstream — neither is ours to retry.
        OperationCanceledException when ct.IsCancellationRequested => false,
        RateLimitException => false,
        HttpRequestException => true,
        // A per-attempt timeout surfaces as a cancellation that isn't the caller's token.
        TaskCanceledException or OperationCanceledException => true,
        _ => false,
    };

    /// <summary>Exponential backoff with jitter, so parallel callers don't retry in lockstep.</summary>
    private TimeSpan DelayFor(int attempt)
    {
        var backoff = _baseDelay * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 0.3 + 0.85; // ±15%
        return backoff * jitter;
    }
}
