using System.Net;
using Mangarr.Core.Http;

namespace Mangarr.Core.Tests;

public class RateLimitDetectingHandlerTests
{
    /// <summary>A canned-response inner handler; every request gets the same message.</summary>
    private sealed class StubHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder());
    }

    private static HttpClient ClientReturning(Func<HttpResponseMessage> responder, bool treat503AsRateLimit = true) =>
        new(new RateLimitDetectingHandler(treat503AsRateLimit) { InnerHandler = new StubHandler(responder) });

    private static Task<HttpResponseMessage> Get(HttpClient client) =>
        client.GetAsync("https://example.test/search?q=x");

    [Fact]
    public async Task Throws_On_429_Carrying_RetryAfter()
    {
        var client = ClientReturning(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        });

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => Get(client));
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Contains("example.test", ex.Message);
    }

    [Fact]
    public async Task Throws_On_503_By_Default()
    {
        var client = ClientReturning(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<RateLimitException>(() => Get(client));
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task Passes_503_Through_When_Challenge_Aware()
    {
        // Cloudflare serves its challenge as 503; ChallengeAwareFetcher must see the status itself
        // so it can hand off to FlareSolverr rather than treating it as a rate limit.
        var client = ClientReturning(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), treat503AsRateLimit: false);

        var response = await Get(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Still_Throws_On_429_When_Challenge_Aware()
    {
        var client = ClientReturning(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests), treat503AsRateLimit: false);

        await Assert.ThrowsAsync<RateLimitException>(() => Get(client));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Leaves_Other_Statuses_Alone(HttpStatusCode status)
    {
        var client = ClientReturning(() => new HttpResponseMessage(status));

        var response = await Get(client);
        Assert.Equal(status, response.StatusCode);
    }
}
