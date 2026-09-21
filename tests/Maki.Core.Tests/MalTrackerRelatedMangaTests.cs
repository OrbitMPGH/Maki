using System.Net;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;

namespace Maki.Core.Tests;

/// <summary>
/// MyAnimeList's v2 API has no anime-to-manga relation field at all - <c>related_manga</c> on the
/// anime endpoint always comes back empty - so <see cref="MalTracker.RelatedMangaAsync"/> resolves
/// through AniList's public GraphQL by the anime's MAL id instead. These pin the JSON parsing and the
/// 429 back-off, since nothing else exercises that path.
/// </summary>
public class MalTrackerRelatedMangaTests
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
            Task.FromResult<ScrobbleToken?>(null);
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
            "https://kitsu.test/api/edge", "https://kitsu.test/api/oauth"));

    [Fact]
    public async Task Picks_the_adaptation_manga_off_AniLists_relations()
    {
        const string json = """
            {"data":{"Media":{"id":21,"relations":{"edges":[
              {"relationType":"ADAPTATION","node":{"id":100937,"idMal":111512,"type":"MANGA","format":"MANGA"}},
              {"relationType":"SEQUEL","node":{"id":5,"idMal":6,"type":"ANIME","format":"TV"}}
            ]}}}}
            """;
        var handler = new RecordingHandler().Then(HttpStatusCode.OK, json);

        var result = await Build(handler).RelatedMangaAsync(userId: 1, animeId: 35849);

        Assert.NotNull(result);
        Assert.Equal(100937, result!.AniListMangaId);
        Assert.Equal(111512, result.MalMangaId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://anilist.test/graphql", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task No_AniList_entry_for_the_MAL_id_returns_null()
    {
        var handler = new RecordingHandler().Then(HttpStatusCode.OK, """{"data":{"Media":null}}""");

        var result = await Build(handler).RelatedMangaAsync(userId: 1, animeId: 999999);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_2xx_GraphQL_errors_array_is_treated_as_a_transport_failure()
    {
        var handler = new RecordingHandler().Then(HttpStatusCode.OK, """{"errors":[{"message":"bad request"}]}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => Build(handler).RelatedMangaAsync(userId: 1, animeId: 1));
    }

    [Fact]
    public async Task A_404_with_a_GraphQL_errors_array_is_a_real_not_found()
    {
        var handler = new RecordingHandler()
            .Then(HttpStatusCode.NotFound, """{"errors":[{"message":"Not Found."}]}""");

        var result = await Build(handler).RelatedMangaAsync(userId: 1, animeId: 999999);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_500_is_a_transport_failure()
    {
        var handler = new RecordingHandler().Then(HttpStatusCode.InternalServerError, "internal error");

        await Assert.ThrowsAsync<HttpRequestException>(() => Build(handler).RelatedMangaAsync(userId: 1, animeId: 1));
    }

    [Fact]
    public async Task A_429_is_waited_out_once_and_then_retried()
    {
        const string json = """{"data":{"Media":{"id":1,"relations":{"edges":[]}}}}""";
        var handler = new RecordingHandler()
            .Then(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            })
            .Then(HttpStatusCode.OK, json);

        var result = await Build(handler).RelatedMangaAsync(userId: 1, animeId: 1);

        Assert.Null(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_second_429_gives_up_rather_than_waiting_again()
    {
        var handler = new RecordingHandler()
            .Then(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            })
            .Then(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            });

        await Assert.ThrowsAsync<HttpRequestException>(() => Build(handler).RelatedMangaAsync(userId: 1, animeId: 1));
        Assert.Equal(2, handler.Requests.Count);
    }
}
