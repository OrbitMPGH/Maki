using System.Net;
using Maki.Core.Http;

namespace Maki.Core.Tests;

public class RateLimitingHandlerSkipTests
{
    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_request_marked_skip_does_not_wait_for_a_permit()
    {
        var limiter = new System.Threading.RateLimiting.ConcurrencyLimiter(new System.Threading.RateLimiting.ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 10,
        });
        using var held = await limiter.AcquireAsync(1);
        using var client = new HttpClient(new RateLimitingHandler(limiter) { InnerHandler = new Ok() });

        var queued = client.GetAsync("http://lan.test/a");
        using var probe = new HttpRequestMessage(HttpMethod.Get, "http://lan.test/b");
        probe.Options.Set(RateLimitingHandler.Skip, true);
        using var answered = await client.SendAsync(probe).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.False(queued.IsCompleted);
        held.Dispose();
        (await queued.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
