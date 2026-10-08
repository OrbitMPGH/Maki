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

    private static HttpClient Client() =>
        new(new PerHostRateLimitingHandler(new HostRateLimiters(
            () => RateLimitingHandler.TokenBucket(1, TimeSpan.FromHours(1))))
        {
            InnerHandler = new OkHandler()
        });

    [Fact]
    public async Task A_spent_budget_on_one_host_does_not_delay_another_host()
    {
        using var client = Client();
        await client.GetAsync("https://a.example/one");

        var queuedOnA = client.GetAsync("https://a.example/two");
        var onB = client.GetAsync("https://b.example/one");

        Assert.Same(onB, await Task.WhenAny(queuedOnA, onB, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.False(queuedOnA.IsCompleted);
    }

    [Fact]
    public async Task Requests_to_the_same_host_share_one_budget()
    {
        using var client = Client();
        await client.GetAsync("https://a.example/one");

        var second = client.GetAsync("https://A.example/two");

        await Task.Delay(200);
        Assert.False(second.IsCompleted);
    }
}
