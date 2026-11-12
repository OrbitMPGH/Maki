using System.Net;
using Maki.Api.Controllers;
using Maki.Core.Http;
using Maki.Sources.Common;

namespace Maki.Api.Services;

/// <summary>
/// Fetches one image for the "pick a poster" actions: a source's cover or a URL an admin typed.
/// Both are a server-side fetch of an address the caller chose, so this goes through the
/// <c>covers</c> client (public addresses only, no automatic redirects) and follows redirects by
/// hand, asking <c>allowHop</c> about every address before it is requested.
/// </summary>
public class CoverFetcher(IHttpClientFactory httpClientFactory, ILogger<CoverFetcher> logger)
{
    internal const int MaxRedirects = 3;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public enum Outcome
    {
        Fetched,
        Refused,
        Failed,
        TooLarge,
    }

    /// <param name="Reason">For a failure: what went wrong, in plain English for the log and the error text.</param>
    public readonly record struct Result(Outcome Outcome, byte[]? Bytes = null, string? Reason = null);

    public static bool IsWebAddress(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task<Result> FetchAsync(
        Uri target, Func<Uri, bool> allowHop, int maxBytes, Uri? referer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var client = httpClientFactory.CreateClient("covers");
        var current = target;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsWebAddress(current) || !allowHop(current))
            {
                logger.LogWarning("Refused a cover fetch from {Host}", current.Host);
                return new Result(Outcome.Refused, Reason: "not an address Maki may fetch");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (referer is not null)
            {
                request.Headers.Referrer = referer;
            }

            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent.Value);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (Exception ex) when (IsBlocked(ex))
            {
                logger.LogWarning("Refused a cover fetch from {Host}: not a public address", current.Host);
                return new Result(Outcome.Refused, Reason: "not a public address");
            }
            catch (Exception ex) when (ex is HttpRequestException ||
                                       (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogInformation(ex, "Cover fetch from {Host} failed", current.Host);
                return new Result(Outcome.Failed,
                    Reason: ex is OperationCanceledException ? "timed out" : "the connection failed");
            }

            using var _ = response;
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest &&
                response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new Result(Outcome.Failed, Reason: $"HTTP {(int)response.StatusCode}");
            }

            try
            {
                var bytes = await SearchController.ReadCappedAsync(response.Content, maxBytes, timeout.Token);
                return bytes is null ? new Result(Outcome.TooLarge) : new Result(Outcome.Fetched, bytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                       (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogInformation(ex, "Reading the cover from {Host} failed", current.Host);
                return new Result(Outcome.Failed,
                    Reason: ex is OperationCanceledException ? "timed out" : "the connection failed");
            }
        }

        return new Result(Outcome.Failed, Reason: "too many redirects");
    }

    private static bool IsBlocked(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is BlockedDestinationException)
            {
                return true;
            }
        }

        return false;
    }
}
