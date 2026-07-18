using System.Net;

namespace Maki.Core.Http;

/// <summary>
/// Turns a source's 429/503 responses into <see cref="RateLimitException"/> for every call on the
/// client, not just page downloads.
/// <para>
/// Without this, only <c>PageDownloader</c> recognised a rate limit; the same 429 during a search
/// or chapter-list sync surfaced as a bare <see cref="HttpRequestException"/> and was reported as
/// an ordinary failure, so the source got hammered again on the next pass instead of backing off.
/// </para>
/// </summary>
/// <param name="treat503AsRateLimit">
/// False for clients that fetch anti-bot-protected sites: Cloudflare serves its challenge with
/// 503, and <see cref="ChallengeAwareFetcher"/> needs to see that status to hand off to
/// FlareSolverr. 429 is unambiguous and always converted.
/// </param>
public class RateLimitDetectingHandler(bool treat503AsRateLimit = true) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (!IsRateLimited(response.StatusCode))
        {
            return response;
        }

        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var host = request.RequestUri?.Host;
        var status = (int)response.StatusCode;
        response.Dispose();

        throw new RateLimitException($"Rate limited by {host} (HTTP {status})", retryAfter);
    }

    private bool IsRateLimited(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests ||
        (treat503AsRateLimit && status == HttpStatusCode.ServiceUnavailable);
}
