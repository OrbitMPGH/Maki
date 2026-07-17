using System.Net;
using Mangarr.Core.Http;

namespace Mangarr.Core.Tests;

public class RateLimitDetectorTests
{
    [Fact]
    public void Detects_RateLimitException_And_Keeps_RetryAfter()
    {
        var ex = new RateLimitException("slow down", TimeSpan.FromSeconds(30));

        Assert.True(RateLimitDetector.IsRateLimit(ex, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Detects_Bare_HttpRequestException_With_RateLimit_Status(HttpStatusCode status)
    {
        var ex = new HttpRequestException("boom", null, status);

        Assert.True(RateLimitDetector.IsRateLimit(ex, out var retryAfter));
        Assert.Null(retryAfter);
    }

    [Fact]
    public void Finds_RateLimit_Wrapped_In_Another_Exception()
    {
        var ex = new InvalidOperationException("sync failed", new RateLimitException("429", TimeSpan.FromSeconds(5)));

        Assert.True(RateLimitDetector.IsRateLimit(ex, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(5), retryAfter);
    }

    [Fact]
    public void Finds_RateLimit_Inside_AggregateException()
    {
        var ex = new AggregateException(
            new InvalidOperationException("unrelated"),
            new RateLimitException("429", TimeSpan.FromSeconds(7)));

        Assert.True(RateLimitDetector.IsRateLimit(ex, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(7), retryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void Ignores_Other_Http_Failures(HttpStatusCode status)
    {
        Assert.False(RateLimitDetector.IsRateLimit(new HttpRequestException("boom", null, status), out _));
    }

    [Fact]
    public void Ignores_Unrelated_Exceptions()
    {
        Assert.False(RateLimitDetector.IsRateLimit(new InvalidOperationException("nope"), out _));
    }
}
