using System.Net;
using Maki.Core.Http;

namespace Maki.Core.Tests;

public class TransientRetryHandlerTests
{
    /// <summary>Replays a scripted sequence of outcomes and counts how many attempts were made.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(step());
        }
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode status) => () => new HttpResponseMessage(status);
    private static Func<HttpResponseMessage> Throws() => () => throw new HttpRequestException("connection refused");

    private static (HttpClient Client, ScriptedHandler Inner) Build(params Func<HttpResponseMessage>[] script)
    {
        var inner = new ScriptedHandler(script);
        // A tiny base delay keeps the backoff from making the suite slow.
        var retry = new TransientRetryHandler(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1))
        {
            InnerHandler = inner,
        };
        return (new HttpClient(retry), inner);
    }

    [Fact]
    public async Task Retries_Until_Success_And_Returns_It()
    {
        var (client, inner) = Build(Throws(), Status(HttpStatusCode.InternalServerError), Status(HttpStatusCode.OK));

        var response = await client.GetAsync("https://example.test/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Gives_Up_After_MaxAttempts_And_Returns_Last_Response()
    {
        var (client, inner) = Build(Status(HttpStatusCode.BadGateway));

        var response = await client.GetAsync("https://example.test/x");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Rethrows_When_Every_Attempt_Throws()
    {
        var (client, inner) = Build(Throws());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.test/x"));
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Does_Not_Retry_Success()
    {
        var (client, inner) = Build(Status(HttpStatusCode.OK));

        await client.GetAsync("https://example.test/x");

        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task Does_Not_Retry_Permanent_Failures(HttpStatusCode status)
    {
        var (client, inner) = Build(Status(status));

        await client.GetAsync("https://example.test/x");

        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Does_Not_Retry_Rate_Limits(HttpStatusCode status)
    {
        // Rate limits are the cooldown's job; retrying just walks into the same wall.
        var (client, inner) = Build(Status(status));

        await client.GetAsync("https://example.test/x");

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Does_Not_Retry_Post()
    {
        // Replaying a POST could add the same torrent twice.
        var (client, inner) = Build(Status(HttpStatusCode.InternalServerError));

        await client.PostAsync("https://example.test/x", new StringContent("body"));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Does_Not_Retry_When_Caller_Cancels()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedHandler(() => throw new OperationCanceledException(cts.Token));
        var retry = new TransientRetryHandler(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1))
        {
            InnerHandler = inner,
        };
        var client = new HttpClient(retry);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("https://example.test/x", cts.Token));
        Assert.Equal(1, inner.Calls);
    }
}
