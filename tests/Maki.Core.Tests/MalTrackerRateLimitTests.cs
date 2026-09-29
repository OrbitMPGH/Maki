using System.Net;
using System.Net.Http.Headers;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

/// <summary>
/// <see cref="MalTracker.RequestAsync"/>'s 429 handling on MAL's own API (distinct from the AniList
/// back-off in <see cref="MalTrackerRelatedMangaTests"/>): it must wait out a short
/// <c>Retry-After</c>, and give up right away rather than sitting through a long one.
/// </summary>
public class MalTrackerRateLimitTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler Then(Func<HttpResponseMessage> respond)
        {
            _responses.Enqueue(respond);
            return this;
        }

        public RecordingHandler Then(HttpStatusCode status, string json) =>
            Then(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TokenStore : IScrobbleTokenStore
    {
        public Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default) =>
            Task.FromResult<ScrobbleToken?>(new ScrobbleToken
            {
                UserId = userId,
                Service = service,
                AccessToken = "token",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
        public Task SaveAsync(ScrobbleToken token, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int userId, string service, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static MalTracker Build(RecordingHandler handler) => new(
        new Factory(handler),
        new Settings(),
        new TokenStore(),
        new ScrobbleTrackerOptions(
            "https://anilist.test/graphql", "https://anilist.test/oauth",
            "https://mal.test", "https://mal.test/oauth",
            "https://mangabaka.test",
            "https://kitsu.test/api/edge", "https://kitsu.test/api/oauth"),
        NullLogger<MalTracker>.Instance);

    private const string MangaJson = """{"id":1,"title":"Test"}""";

    private static HttpResponseMessage TooManyRequests(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }

        return response;
    }

    private static HttpResponseMessage TooManyRequestsAt(DateTimeOffset retryAfterDate)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfterDate);
        return response;
    }

    [Fact]
    public async Task A_short_Retry_After_is_waited_out_and_then_retried()
    {
        var handler = new RecordingHandler()
            .Then(() => TooManyRequests(TimeSpan.FromMilliseconds(1)))
            .Then(HttpStatusCode.OK, MangaJson);

        var started = DateTime.UtcNow;
        var result = await Build(handler).GetEntryAsync(userId: 1, remoteId: "1");
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal("Test", result.Title);
        Assert.Equal(2, handler.Requests.Count);
        // Would sit through the old fixed 5s fallback if the actual Retry-After were ignored.
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"took {elapsed}");
    }

    [Fact]
    public async Task No_Retry_After_falls_back_to_a_short_fixed_wait()
    {
        var handler = new RecordingHandler()
            .Then(() => TooManyRequests(null))
            .Then(HttpStatusCode.OK, MangaJson);

        var started = DateTime.UtcNow;
        var result = await Build(handler).GetEntryAsync(userId: 1, remoteId: "1");
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal("Test", result.Title);
        Assert.Equal(2, handler.Requests.Count);
        // The fixed fallback is 5s and the cap is 30s; well under the cap confirms the fallback path,
        // not a coincidentally-short cap failure.
        Assert.True(elapsed >= TimeSpan.FromSeconds(4.5), $"took {elapsed}");
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"took {elapsed}");
    }

    [Fact]
    public async Task A_Retry_After_date_already_in_the_past_does_not_wait_forever()
    {
        var handler = new RecordingHandler()
            .Then(() => TooManyRequestsAt(DateTimeOffset.UtcNow.AddSeconds(-30)))
            .Then(HttpStatusCode.OK, MangaJson);

        var started = DateTime.UtcNow;
        var result = await Build(handler).GetEntryAsync(userId: 1, remoteId: "1");
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal("Test", result.Title);
        Assert.Equal(2, handler.Requests.Count);
        // A negative wait must clamp to zero. Unclamped, Task.Delay throws ArgumentOutOfRangeException
        // (or, at exactly -1ms, waits forever).
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"took {elapsed}");
    }

    [Fact]
    public async Task A_Retry_After_beyond_the_cap_throws_immediately_without_retrying()
    {
        var handler = new RecordingHandler().Then(() => TooManyRequests(TimeSpan.FromSeconds(31)));

        await Assert.ThrowsAsync<TrackerException>(() => Build(handler).GetEntryAsync(userId: 1, remoteId: "1"));

        // No second attempt: retrying would mean sitting through the wait for nothing.
        Assert.Single(handler.Requests);
    }
}
