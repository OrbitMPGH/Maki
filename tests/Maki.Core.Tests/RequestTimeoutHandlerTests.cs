using System.Net;
using System.Threading.RateLimiting;
using Maki.Core.Http;
using Microsoft.Extensions.Time.Testing;

namespace Maki.Core.Tests;

public class RequestTimeoutHandlerTests
{
    private readonly FakeTimeProvider _time = new();

    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class Hangs : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_request_that_hangs_times_out_like_the_client_would()
    {
        var hangs = new Hangs();
        using var client = new HttpClient(new RequestTimeoutHandler(TimeSpan.FromSeconds(30), _time) { InnerHandler = hangs })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var request = client.GetAsync("http://lan.test/a");
        await hangs.Entered;
        _time.Advance(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => request);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task The_caller_cancelling_stays_a_plain_cancellation()
    {
        var hangs = new Hangs();
        using var client = new HttpClient(new RequestTimeoutHandler(TimeSpan.FromMinutes(1), _time) { InnerHandler = hangs });
        using var cts = new CancellationTokenSource();

        var request = client.GetAsync("http://lan.test/a", cts.Token);
        await hangs.Entered;
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Null(ex.InnerException as TimeoutException);
    }

    [Fact]
    public async Task Time_spent_queued_for_a_permit_does_not_count_against_the_request()
    {
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 10 });
        using var held = await limiter.AcquireAsync(1);
        var chain = new RateLimitingHandler(limiter)
        {
            InnerHandler = new RequestTimeoutHandler(TimeSpan.FromSeconds(30), _time) { InnerHandler = new Ok() },
        };
        using var client = new HttpClient(chain) { Timeout = Timeout.InfiniteTimeSpan };

        var queued = client.GetAsync("http://lan.test/a");
        _time.Advance(TimeSpan.FromMinutes(5));
        held.Dispose();

        using var answered = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    [Fact]
    public async Task An_inner_timeout_passes_through_an_outer_one_untouched()
    {
        var hangs = new Hangs();
        var inner = new RequestTimeoutHandler(TimeSpan.FromSeconds(30), _time) { InnerHandler = hangs };
        using var client = new HttpClient(new RequestTimeoutHandler(TimeSpan.FromMinutes(10), _time) { InnerHandler = inner })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var request = client.GetAsync("http://lan.test/a");
        await hangs.Entered;
        _time.Advance(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => request);

        Assert.DoesNotContain("600", ex.Message);
    }
}
