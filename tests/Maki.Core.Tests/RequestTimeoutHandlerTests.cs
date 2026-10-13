using System.Net;
using System.Threading.RateLimiting;
using Maki.Core.Http;

namespace Maki.Core.Tests;

public class RequestTimeoutHandlerTests
{
    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class Hangs : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_request_that_hangs_times_out_like_the_client_would()
    {
        using var client = new HttpClient(new RequestTimeoutHandler(TimeSpan.FromMilliseconds(100)) { InnerHandler = new Hangs() })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("http://lan.test/a"));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task The_caller_cancelling_stays_a_plain_cancellation()
    {
        using var client = new HttpClient(new RequestTimeoutHandler(TimeSpan.FromMinutes(1)) { InnerHandler = new Hangs() });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("http://lan.test/a", cts.Token));
        Assert.Null(ex.InnerException as TimeoutException);
    }

    [Fact]
    public async Task Time_spent_queued_for_a_permit_does_not_count_against_the_request()
    {
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 10 });
        using var held = await limiter.AcquireAsync(1);
        var chain = new RateLimitingHandler(limiter)
        {
            InnerHandler = new RequestTimeoutHandler(TimeSpan.FromMilliseconds(200)) { InnerHandler = new Ok() },
        };
        using var client = new HttpClient(chain) { Timeout = Timeout.InfiniteTimeSpan };

        var queued = client.GetAsync("http://lan.test/a");
        await Task.Delay(500);
        held.Dispose();

        using var answered = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    [Fact]
    public async Task An_inner_timeout_passes_through_an_outer_one_untouched()
    {
        var inner = new RequestTimeoutHandler(TimeSpan.FromMilliseconds(100)) { InnerHandler = new Hangs() };
        using var client = new HttpClient(new RequestTimeoutHandler(TimeSpan.FromMinutes(10)) { InnerHandler = inner })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("http://lan.test/a"));

        Assert.DoesNotContain("600", ex.Message);
    }
}
