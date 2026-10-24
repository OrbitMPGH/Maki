using System.Net;
using Maki.Core.Http;
using Maki.Sources.Common;

namespace Maki.Sources.Tests;

public class PerHostRateLimitingHandlerTests
{
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    // One request an hour, so a second request to the same host stays queued for the whole test.
    private readonly HostRateLimiters _limiters = new(() => RateLimitingHandler.TokenBucket(1, TimeSpan.FromHours(1)));

    private HttpClient Client() =>
        new(new PerHostRateLimitingHandler(_limiters) { InnerHandler = new OkHandler() });

    private long QueuedOn(string host) => _limiters.For(host).GetStatistics()!.CurrentQueuedCount;

    [Fact]
    public async Task A_spent_budget_on_one_host_does_not_delay_another_host()
    {
        using var client = Client();
        await client.GetAsync("https://a.example/one");

        var queuedOnA = client.GetAsync("https://a.example/two");
        using var onB = await client.GetAsync("https://b.example/one").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, onB.StatusCode);
        Assert.False(queuedOnA.IsCompleted);
        Assert.Equal(1, QueuedOn("a.example"));
    }

    [Fact]
    public async Task Requests_to_the_same_host_share_one_budget()
    {
        using var client = Client();
        await client.GetAsync("https://a.example/one");

        var second = client.GetAsync("https://A.example/two");

        Assert.Equal(1, QueuedOn("a.example"));
        Assert.False(second.IsCompleted);
    }
}
