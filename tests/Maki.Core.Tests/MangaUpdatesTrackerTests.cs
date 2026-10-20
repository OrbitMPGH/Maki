using System.Net;
using System.Text;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Maki.Core.Tests;

public class MangaUpdatesTrackerTests
{
    private const int UserId = 1;
    private const string OnePiece = "pb8uwds";
    private const long OnePieceId = 55099564912;
    private const string Base = "https://mu.test/v1";

    /// <summary>
    /// Answers by method and exact API path, the most recently added rule first. Each rule hands out
    /// its responses in order and repeats the last one, so "412 then 200" is two responses on one rule.
    /// </summary>
    private sealed class Handler : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Path, Func<HttpRequestMessage, HttpResponseMessage>[] Responses)> _rules = [];
        private readonly Dictionary<int, int> _served = [];

        public List<(HttpMethod Method, string Path, string Body, string? Bearer)> Requests { get; } = [];

        public Handler On(HttpMethod method, string path, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _rules.Add((method, path, responses));
            return this;
        }

        public Handler On(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
            On(method, path, _ => Json(json, status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath["/v1".Length..];
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, path, body, request.Headers.Authorization?.Parameter));
            // Last rule wins, so a test can override one of the shared defaults.
            for (var i = _rules.Count - 1; i >= 0; i--)
            {
                var (method, rulePath, responses) = _rules[i];
                if (method == request.Method && rulePath == path)
                {
                    var n = _served.GetValueOrDefault(i);
                    _served[i] = n + 1;
                    return responses[Math.Min(n, responses.Length - 1)](request);
                }
            }

            return Json("""{"status":"exception","reason":"unmatched"}""", HttpStatusCode.NotFound);
        }

        public IEnumerable<(HttpMethod Method, string Path, string Body, string? Bearer)> To(HttpMethod method, string path) =>
            Requests.Where(r => r.Method == method && r.Path == path);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class UserSettings(Dictionary<string, string> values) : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
        {
            values[key] = value ?? "";
            return Task.CompletedTask;
        }
    }

    private sealed class TokenStore : IScrobbleTokenStore
    {
        public ScrobbleToken? Token { get; set; }

        public Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default) =>
            Task.FromResult(Token);

        public Task SaveAsync(ScrobbleToken token, CancellationToken ct = default)
        {
            Token = token;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int userId, string service, CancellationToken ct = default)
        {
            Token = null;
            return Task.CompletedTask;
        }
    }

    private sealed record Rig(MangaUpdatesTracker Tracker, TokenStore Tokens, FakeTimeProvider Time, List<TimeSpan> Delays);

    private static Rig Build(Handler handler, bool loggedIn = true)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var tokens = new TokenStore
        {
            Token = loggedIn
                ? new ScrobbleToken
                {
                    UserId = UserId,
                    Service = "mangaupdates",
                    AccessToken = "old-token",
                    ExpiresAt = time.GetUtcNow().UtcDateTime.AddDays(20),
                    Username = "reader",
                }
                : null,
        };
        var delays = new List<TimeSpan>();
        var tracker = new MangaUpdatesTracker(
            new Factory(handler),
            new UserSettings(new Dictionary<string, string>
            {
                [SettingKeys.ScrobbleMangaUpdatesUsername] = "reader",
                [SettingKeys.ScrobbleMangaUpdatesPassword] = "pw",
            }),
            tokens,
            new ScrobbleTrackerOptions(MangaUpdatesApiUrl: Base),
            NullLogger<MangaUpdatesTracker>.Instance)
        {
            Time = time,
            Delay = (d, _) =>
            {
                delays.Add(d);
                time.Advance(d);
                return Task.CompletedTask;
            },
        };
        return new Rig(tracker, tokens, time, delays);
    }

    private static string Jwt(DateTimeOffset expires)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"HS256"}""")}.{B64($$$"""{"exp":{{{expires.ToUnixTimeSeconds()}}}}""")}.sig";
    }

    private static string LoginJson(string token) =>
        $$$"""{"status":"success","reason":"Successfully logged in","context":{"session_token":"{{{token}}}","uid":7}}""";

    private const string ProfileJson = """{"user_id":7,"username":"MuReader"}""";

    private static string ListedJson(int listId, int chapter = 3, int volume = 0) => $$$"""
        {"series":{"id":{{{OnePieceId}}},"url":"https://www.mangaupdates.com/series/pb8uwds/one-piece","title":"One Piece"},
         "list_id":{{{listId}}},"list_type":"read","status":{"volume":{{{volume}}},"chapter":{{{chapter}}}}}
        """;

    private static string SeriesJson(bool completed = false, int latest = 1130) =>
        $$$"""{"series_id":{{{OnePieceId}}},"title":"One Piece","url":"https://www.mangaupdates.com/series/pb8uwds/one-piece","latest_chapter":{{{latest}}},"completed":{{{(completed ? "true" : "false")}}}}""";

    private const string EmptyListPage = """{"total_hits":0,"page":1,"per_page":100,"results":[]}""";

    private static Handler WithSeries(bool completed = false, int latest = 1130) => new Handler()
        .On(HttpMethod.Get, $"/series/{OnePieceId}", SeriesJson(completed, latest))
        .On(HttpMethod.Post, "/lists/0/search", EmptyListPage)
        .On(HttpMethod.Post, "/lists/1/search", EmptyListPage)
        .On(HttpMethod.Post, "/lists/2/search", EmptyListPage)
        .On(HttpMethod.Post, "/lists/3/search", EmptyListPage)
        .On(HttpMethod.Post, "/lists/4/search", EmptyListPage);

    private static Handler Listed(int listId, int chapter = 3, int volume = 0) => WithSeries()
        .On(HttpMethod.Get, $"/lists/series/{OnePieceId}", ListedJson(listId, chapter, volume));

    private static Handler Unlisted() => WithSeries()
        .On(HttpMethod.Get, $"/lists/series/{OnePieceId}", """{"status":"exception","reason":"not on list"}""",
            HttpStatusCode.NotFound);

    // ---- ids ----

    [Fact]
    public void SlugAndSeriesIdRoundTrip()
    {
        Assert.Equal(OnePiece, MangaUpdatesIds.ToSlug(OnePieceId));
        Assert.True(MangaUpdatesIds.TryToSeriesId(OnePiece, out var id));
        Assert.Equal(OnePieceId, id);
        Assert.True(MangaUpdatesIds.TryToSeriesId("PB8UWDS", out var upper));
        Assert.Equal(OnePieceId, upper);
        Assert.True(MangaUpdatesIds.TryToSeriesId(MangaUpdatesIds.ToSlug(long.MaxValue), out var max));
        Assert.Equal(long.MaxValue, max);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pb8-uwds")]
    [InlineData("zzzzzzzzzzzzzz")]
    public void RejectsMalformedSlugs(string slug) => Assert.False(MangaUpdatesIds.TryToSeriesId(slug, out _));

    [Theory]
    [InlineData("pb8uwds", "pb8uwds")]
    [InlineData(" PB8UWDS ", "pb8uwds")]
    [InlineData("https://www.mangaupdates.com/series/pb8uwds/one-piece", "pb8uwds")]
    [InlineData("mangaupdates.com/series/PB8UWDS", "pb8uwds")]
    [InlineData("6028577", "6028577")]
    [InlineData("https://www.mangaupdates.com/series/6028577/some-title", "6028577")]
    public void ParsesSlugsAndSeriesUrls(string input, string expected)
    {
        Assert.True(MangaUpdatesIds.TryParse(input, out var slug));
        Assert.Equal(expected, slug);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("602857")]
    [InlineData("https://www.mangaupdates.com/series.html?id=12345")]
    [InlineData("https://www.mangaupdates.com/releases.html")]
    [InlineData("https://mangaupdates.com.evil.example/series/pb8uwds")]
    public void RefusesLegacyIdsAndForeignUrls(string input) => Assert.False(MangaUpdatesIds.TryParse(input, out _));

    [Fact]
    public void ReadsTokenExpiryFromTheJwt()
    {
        var expires = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expires.UtcDateTime, MangaUpdatesTracker.TokenExpiry(Jwt(expires)));
        Assert.Null(MangaUpdatesTracker.TokenExpiry("not-a-jwt"));
    }

    // ---- auth ----

    [Fact]
    public async Task LogsInLazilyOnce_AndStoresTheProfileName()
    {
        var expires = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var jwt = Jwt(expires);
        var handler = new Handler()
            .On(HttpMethod.Put, "/account/login", LoginJson(jwt))
            .On(HttpMethod.Get, "/account/profile", ProfileJson);
        var rig = Build(handler, loggedIn: false);

        Assert.True(await rig.Tracker.AuthenticatedAsync(UserId));
        Assert.True(await rig.Tracker.AuthenticatedAsync(UserId));

        var login = Assert.Single(handler.To(HttpMethod.Put, "/account/login"));
        Assert.Contains("\"username\":\"reader\"", login.Body);
        Assert.Contains("\"password\":\"pw\"", login.Body);
        Assert.Equal(jwt, Assert.Single(handler.To(HttpMethod.Get, "/account/profile")).Bearer);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(jwt, rig.Tokens.Token!.AccessToken);
        Assert.Equal("MuReader", await rig.Tracker.UsernameAsync(UserId));
        Assert.Equal(expires.UtcDateTime, rig.Tokens.Token.ExpiresAt);
    }

    [Fact]
    public async Task AnExpired401LogsInAgainOnce_AndRetriesWithTheNewToken()
    {
        var handler = WithSeries()
            .On(HttpMethod.Put, "/account/login", LoginJson("new-token"))
            .On(HttpMethod.Get, "/account/profile", ProfileJson)
            .On(HttpMethod.Get, $"/lists/series/{OnePieceId}", r => r.Headers.Authorization?.Parameter == "old-token"
                ? Json("""{"status":"exception","reason":"expired"}""", HttpStatusCode.Unauthorized)
                : Json(ListedJson(0)));
        var rig = Build(handler);

        var entry = await rig.Tracker.GetEntryAsync(UserId, OnePiece);

        Assert.Equal(ScrobbleStatus.Reading, entry.Status);
        Assert.Single(handler.To(HttpMethod.Put, "/account/login"));
        Assert.Equal(["old-token", "new-token"],
            handler.To(HttpMethod.Get, $"/lists/series/{OnePieceId}").Select(r => r.Bearer));
    }

    [Fact]
    public async Task ConcurrentFirstLoginsCostOneLogin()
    {
        var handler = new Handler()
            .On(HttpMethod.Put, "/account/login", LoginJson(Jwt(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero))))
            .On(HttpMethod.Get, "/account/profile", ProfileJson);
        var tracker = Build(handler, loggedIn: false).Tracker;

        var results = await Task.WhenAll(
            Task.Run(() => tracker.AuthenticatedAsync(UserId)),
            Task.Run(() => tracker.AuthenticatedAsync(UserId)),
            Task.Run(() => tracker.AuthenticatedAsync(UserId)));

        Assert.All(results, Assert.True);
        Assert.Single(handler.To(HttpMethod.Put, "/account/login"));
    }

    [Fact]
    public async Task A401WhoseReloginFailsDropsTheTokenAndStopsAsking()
    {
        var handler = WithSeries()
            .On(HttpMethod.Put, "/account/login", """{"status":"exception","reason":"bad password"}""",
                HttpStatusCode.Unauthorized)
            .On(HttpMethod.Get, $"/lists/series/{OnePieceId}", """{"status":"exception","reason":"revoked"}""",
                HttpStatusCode.Unauthorized);
        var rig = Build(handler);

        await Assert.ThrowsAnyAsync<TrackerException>(() => rig.Tracker.GetEntryAsync(UserId, OnePiece));

        Assert.Null(rig.Tokens.Token);
        Assert.False(await rig.Tracker.AuthenticatedAsync(UserId));
        await Assert.ThrowsAnyAsync<TrackerException>(() => rig.Tracker.GetEntryAsync(UserId, OnePiece));
        Assert.Single(handler.To(HttpMethod.Put, "/account/login"));
        Assert.Single(handler.To(HttpMethod.Get, $"/lists/series/{OnePieceId}"));
    }

    [Fact]
    public async Task AFailedLoginIsCached_UntilForgotten()
    {
        var handler = new Handler()
            .On(HttpMethod.Put, "/account/login", """{"status":"exception","reason":"bad password"}""",
                HttpStatusCode.Unauthorized);
        var rig = Build(handler, loggedIn: false);

        Assert.False(await rig.Tracker.AuthenticatedAsync(UserId));
        Assert.False(await rig.Tracker.AuthenticatedAsync(UserId));
        await Assert.ThrowsAnyAsync<TrackerException>(() => rig.Tracker.GetEntryAsync(UserId, OnePiece));
        Assert.Single(handler.To(HttpMethod.Put, "/account/login"));

        rig.Tracker.Forget(UserId);
        Assert.False(await rig.Tracker.AuthenticatedAsync(UserId));
        Assert.Equal(2, handler.To(HttpMethod.Put, "/account/login").Count());
    }

    [Fact]
    public async Task NoCredentialsMeansNotAuthenticated_EvenWithAStoredToken()
    {
        var handler = new Handler();
        var tracker = new MangaUpdatesTracker(
            new Factory(handler),
            new UserSettings([]),
            new TokenStore { Token = new ScrobbleToken { UserId = UserId, Service = "mangaupdates", AccessToken = "t" } },
            new ScrobbleTrackerOptions(MangaUpdatesApiUrl: Base),
            NullLogger<MangaUpdatesTracker>.Instance);

        Assert.True(await tracker.ConfiguredAsync());
        Assert.False(await tracker.AuthenticatedAsync(UserId));
        Assert.Empty(handler.Requests);
    }

    // ---- reading entries ----

    [Theory]
    [InlineData(0, ScrobbleStatus.Reading)]
    [InlineData(1, ScrobbleStatus.PlanToRead)]
    [InlineData(2, ScrobbleStatus.Completed)]
    [InlineData(3, ScrobbleStatus.Other)]
    [InlineData(4, ScrobbleStatus.Other)]
    [InlineData(101, ScrobbleStatus.Other)]
    public async Task MapsListsToStatuses(int listId, ScrobbleStatus expected)
    {
        var entry = await (Build(Listed(listId, chapter: 12, volume: 2))).Tracker.GetEntryAsync(UserId, OnePiece);

        Assert.Equal(expected, entry.Status);
        Assert.Equal(12, entry.ProgressChapter);
        Assert.Equal(2, entry.ProgressVolume);
        Assert.Equal("One Piece", entry.Title);
    }

    [Fact]
    public async Task AnUnlistedSeriesHasNoStatus_AndADeadOneIsNotFound()
    {
        var unlisted = await (Build(Unlisted())).Tracker.GetEntryAsync(UserId, OnePiece);
        Assert.Null(unlisted.Status);
        Assert.Equal("One Piece", unlisted.Title);

        var dead = new Handler()
            .On(HttpMethod.Get, $"/lists/series/{OnePieceId}", "{}", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, $"/series/{OnePieceId}", """{"status":"exception","reason":"Series not found"}""",
                HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<TrackerEntryNotFoundException>(
            () => (Build(dead)).Tracker.GetEntryAsync(UserId, OnePiece));
    }

    [Fact]
    public async Task AMalformedStoredIdIsNotFound()
    {
        await Assert.ThrowsAsync<TrackerEntryNotFoundException>(
            () => (Build(new Handler())).Tracker.GetEntryAsync(UserId, "not-a-slug"));
    }

    [Fact]
    public async Task LatestChapterIsOnlyATotalOnceTheSeriesIsComplete()
    {
        var ongoing = await (Build(Unlisted())).Tracker.GetEntryAsync(UserId, OnePiece);
        Assert.Null(ongoing.TotalChapters);
        Assert.True(ongoing.Releasing);

        var done = WithSeries(completed: true, latest: 89)
            .On(HttpMethod.Get, $"/lists/series/{OnePieceId}", "{}", HttpStatusCode.NotFound);
        var finished = await (Build(done)).Tracker.GetEntryAsync(UserId, OnePiece);
        Assert.Equal(89, finished.TotalChapters);
        Assert.False(finished.Releasing);
    }

    [Fact]
    public async Task SeriesInfoIsCachedForSixHours()
    {
        var handler = Unlisted();
        var rig = Build(handler);

        await rig.Tracker.GetEntryAsync(UserId, OnePiece);
        await rig.Tracker.GetEntryAsync(UserId, OnePiece);
        Assert.Single(handler.To(HttpMethod.Get, $"/series/{OnePieceId}"));

        rig.Time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        await rig.Tracker.GetEntryAsync(UserId, OnePiece);
        Assert.Equal(2, handler.To(HttpMethod.Get, $"/series/{OnePieceId}").Count());
    }

    [Fact]
    public async Task ScoreComesFromOneRatingSnapshot_NotARequestPerSeries()
    {
        var handler = Listed(0)
            .On(HttpMethod.Post, "/lists/0/search", $$$"""
                {"total_hits":1,"page":1,"per_page":100,"results":[
                  {"record":{"series":{"id":{{{OnePieceId}}},"title":"One Piece"},"list_id":0,"status":{"chapter":3}},
                   "metadata":{"user_rating":6.5}}]}
                """);
        var rig = Build(handler);

        var first = await rig.Tracker.GetEntryAsync(UserId, OnePiece);
        var second = await rig.Tracker.GetEntryAsync(UserId, OnePiece);

        Assert.Equal(7, first.Score);
        Assert.Equal(7, second.Score);
        Assert.Single(handler.To(HttpMethod.Post, "/lists/0/search"));
        Assert.Single(handler.To(HttpMethod.Post, "/lists/4/search"));
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/rating"));

        rig.Time.Advance(TimeSpan.FromMinutes(16));
        await rig.Tracker.GetEntryAsync(UserId, OnePiece);
        Assert.Equal(2, handler.To(HttpMethod.Post, "/lists/0/search").Count());
    }

    [Fact]
    public async Task ARatingWriteDropsTheSnapshot()
    {
        var handler = Listed(0)
            .On(HttpMethod.Put, $"/series/{OnePieceId}/rating", """{"status":"success"}""");
        var tracker = Build(handler).Tracker;

        await tracker.GetEntryAsync(UserId, OnePiece);
        await tracker.UpdateRatingAsync(UserId, OnePiece, 9);
        await tracker.GetEntryAsync(UserId, OnePiece);

        Assert.Equal(2, handler.To(HttpMethod.Post, "/lists/0/search").Count());
    }

    [Fact]
    public async Task AFailedRatingSnapshotLeavesTheScoreUnknown_WithoutFailingTheRead()
    {
        var handler = Listed(0, chapter: 4)
            .On(HttpMethod.Post, "/lists/2/search", """{"status":"exception","reason":"oops"}""",
                HttpStatusCode.InternalServerError);

        var entry = await Build(handler).Tracker.GetEntryAsync(UserId, OnePiece);

        Assert.Equal(ScrobbleStatus.Reading, entry.Status);
        Assert.Equal(4, entry.ProgressChapter);
        Assert.Null(entry.Score);
    }

    [Fact]
    public async Task AnUnlistedSeriesDoesNotBuildTheRatingSnapshot()
    {
        var handler = Unlisted();
        await (Build(handler)).Tracker.GetEntryAsync(UserId, OnePiece);

        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/search"));
    }

    // ---- writes ----

    [Fact]
    public async Task AnUnlistedSeriesIsAdded_WithChapterAndVolume()
    {
        var handler = Unlisted().On(HttpMethod.Post, "/lists/series", """{"status":"success"}""");
        var tracker = (Build(handler)).Tracker;

        await tracker.GetEntryAsync(UserId, OnePiece);
        await tracker.UpdateAsync(UserId, OnePiece, chapter: 5, volume: 1, ScrobbleStatus.Reading);

        var add = Assert.Single(handler.To(HttpMethod.Post, "/lists/series"));
        Assert.Equal($$$"""[{"series":{"id":{{{OnePieceId}}}},"list_id":0,"status":{"chapter":5,"volume":1}}]""", add.Body);
        Assert.Empty(handler.To(HttpMethod.Post, "/lists/series/update"));
    }

    [Fact]
    public async Task AListedSeriesIsUpdated_AndVolumeIsLeftOutWhenUnknown()
    {
        var handler = Listed(0).On(HttpMethod.Post, "/lists/series/update", """{"status":"success"}""");
        var tracker = (Build(handler)).Tracker;

        await tracker.GetEntryAsync(UserId, OnePiece);
        await tracker.UpdateAsync(UserId, OnePiece, chapter: 9, volume: 0, ScrobbleStatus.Completed);

        var update = Assert.Single(handler.To(HttpMethod.Post, "/lists/series/update"));
        Assert.Equal($$$"""[{"series":{"id":{{{OnePieceId}}}},"list_id":2,"status":{"chapter":9}}]""", update.Body);
        Assert.Empty(handler.To(HttpMethod.Post, "/lists/series"));
    }

    [Fact]
    public async Task AWriteWithoutAPrecedingReadLooksTheEntryUp()
    {
        var handler = Unlisted().On(HttpMethod.Post, "/lists/series", """{"status":"success"}""");

        await (Build(handler)).Tracker.UpdateAsync(UserId, OnePiece, 1, 0, ScrobbleStatus.PlanToRead);

        Assert.Single(handler.To(HttpMethod.Get, $"/lists/series/{OnePieceId}"));
        Assert.Contains("\"list_id\":1", Assert.Single(handler.To(HttpMethod.Post, "/lists/series")).Body);
    }

    [Fact]
    public async Task AnUpdateRefusedBecauseTheEntryWentAwayFallsBackToAnAdd()
    {
        var handler = Listed(0)
            .On(HttpMethod.Post, "/lists/series/update", """{"status":"exception","reason":"Series not on list"}""",
                HttpStatusCode.BadRequest)
            .On(HttpMethod.Post, "/lists/series", """{"status":"success"}""");
        var view = Build(handler);
        var tracker = view.Tracker;

        await tracker.GetEntryAsync(UserId, OnePiece);
        await tracker.UpdateAsync(UserId, OnePiece, 5, 0, ScrobbleStatus.Reading);

        Assert.Single(handler.To(HttpMethod.Post, "/lists/series/update"));
        Assert.Single(handler.To(HttpMethod.Post, "/lists/series"));
        Assert.Contains(view.Delays, d => d >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task KeepStatusLeavesTheEntryOnItsCurrentList()
    {
        var handler = Listed(4).On(HttpMethod.Post, "/lists/series/update", """{"status":"success"}""");
        var tracker = (Build(handler)).Tracker;

        await tracker.GetEntryAsync(UserId, OnePiece);
        await tracker.UpdateAsync(UserId, OnePiece, 7, 0, ScrobbleStatus.Reading, keepStatus: true);

        Assert.Contains("\"list_id\":4", Assert.Single(handler.To(HttpMethod.Post, "/lists/series/update")).Body);
    }

    [Fact]
    public async Task A412IsRetriedOnceAfterFiveSeconds()
    {
        var handler = Listed(0).On(HttpMethod.Post, "/lists/series/update",
            _ => Json("""{"status":"exception","reason":"Five second update delay"}""", HttpStatusCode.PreconditionFailed),
            _ => Json("""{"status":"success"}"""));
        var view = Build(handler);
        var tracker = view.Tracker;

        await tracker.GetEntryAsync(UserId, OnePiece);
        await tracker.UpdateAsync(UserId, OnePiece, 5, 0, ScrobbleStatus.Reading);

        Assert.Equal(2, handler.To(HttpMethod.Post, "/lists/series/update").Count());
        Assert.Equal([TimeSpan.FromSeconds(5)], view.Delays);
    }

    [Fact]
    public async Task ASecond412Fails()
    {
        var handler = Listed(0).On(HttpMethod.Post, "/lists/series/update", "{}", HttpStatusCode.PreconditionFailed);
        var tracker = (Build(handler)).Tracker;

        await tracker.GetEntryAsync(UserId, OnePiece);
        await Assert.ThrowsAnyAsync<TrackerException>(
            () => tracker.UpdateAsync(UserId, OnePiece, 5, 0, ScrobbleStatus.Reading));
        Assert.Equal(2, handler.To(HttpMethod.Post, "/lists/series/update").Count());
    }

    [Fact]
    public async Task ListWritesAreSpacedFiveAndAHalfSecondsApart_RatingsAreNot()
    {
        var handler = Listed(0)
            .On(HttpMethod.Post, "/lists/series/update", """{"status":"success"}""")
            .On(HttpMethod.Put, $"/series/{OnePieceId}/rating", """{"status":"success"}""");
        var view = Build(handler);
        var rig = view;

        await rig.Tracker.GetEntryAsync(UserId, OnePiece);
        await rig.Tracker.UpdateAsync(UserId, OnePiece, 5, 0, ScrobbleStatus.Reading);
        Assert.Empty(view.Delays);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.Tracker.UpdateRatingAsync(UserId, OnePiece, 8);
        Assert.Empty(view.Delays);

        await rig.Tracker.UpdateAsync(UserId, OnePiece, 6, 0, ScrobbleStatus.Reading);
        Assert.Equal([TimeSpan.FromSeconds(3.5)], view.Delays);

        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await rig.Tracker.UpdateAsync(UserId, OnePiece, 7, 0, ScrobbleStatus.Reading);
        Assert.Single(view.Delays);
    }

    [Fact]
    public async Task ConcurrentListWritesQueueBehindTheGate()
    {
        var handler = Listed(0).On(HttpMethod.Post, "/lists/series/update", """{"status":"success"}""");
        var view = Build(handler);
        var tracker = view.Tracker;
        await tracker.GetEntryAsync(UserId, OnePiece);

        await Task.WhenAll(
            Task.Run(() => tracker.UpdateAsync(UserId, OnePiece, 5, 0, ScrobbleStatus.Reading)),
            Task.Run(() => tracker.UpdateAsync(UserId, OnePiece, 6, 0, ScrobbleStatus.Reading)));

        Assert.Equal(2, handler.To(HttpMethod.Post, "/lists/series/update").Count());
        Assert.Equal([MangaUpdatesTracker.WriteSpacing], view.Delays);
    }

    [Fact]
    public async Task RatingsArePutAndCleared()
    {
        var handler = new Handler()
            .On(HttpMethod.Put, $"/series/{OnePieceId}/rating", """{"status":"success"}""")
            .On(HttpMethod.Delete, $"/series/{OnePieceId}/rating", """{"status":"success"}""");
        var tracker = (Build(handler)).Tracker;

        await tracker.UpdateRatingAsync(UserId, OnePiece, 8);
        await tracker.UpdateRatingAsync(UserId, OnePiece, 0);

        Assert.Equal("""{"rating":8}""", Assert.Single(handler.To(HttpMethod.Put, $"/series/{OnePieceId}/rating")).Body);
        Assert.Single(handler.To(HttpMethod.Delete, $"/series/{OnePieceId}/rating"));
    }

    [Fact]
    public async Task ARating401RightAfterLoginIsARoleProblem_NotAnExpiredToken()
    {
        var handler = new Handler()
            .On(HttpMethod.Put, "/account/login", LoginJson("fresh-token"))
            .On(HttpMethod.Get, "/account/profile", ProfileJson)
            .On(HttpMethod.Put, $"/series/{OnePieceId}/rating", """{"status":"exception","reason":"role"}""",
                HttpStatusCode.Unauthorized);
        var rig = Build(handler, loggedIn: false);
        Assert.True(await rig.Tracker.AuthenticatedAsync(UserId));

        await Assert.ThrowsAnyAsync<TrackerException>(() => rig.Tracker.UpdateRatingAsync(UserId, OnePiece, 8));

        Assert.Single(handler.To(HttpMethod.Put, "/account/login"));
        Assert.Single(handler.To(HttpMethod.Put, $"/series/{OnePieceId}/rating"));
    }

    [Fact]
    public async Task ARating401OnAnOldTokenLogsInAgainOnce()
    {
        var handler = new Handler()
            .On(HttpMethod.Put, "/account/login", LoginJson("new-token"))
            .On(HttpMethod.Get, "/account/profile", ProfileJson)
            .On(HttpMethod.Put, $"/series/{OnePieceId}/rating", r => r.Headers.Authorization?.Parameter == "old-token"
                ? Json("{}", HttpStatusCode.Unauthorized)
                : Json("""{"status":"success"}"""));

        await (Build(handler)).Tracker.UpdateRatingAsync(UserId, OnePiece, 8);

        Assert.Single(handler.To(HttpMethod.Put, "/account/login"));
        Assert.Equal(2, handler.To(HttpMethod.Put, $"/series/{OnePieceId}/rating").Count());
    }

    // ---- search and upstream blocks ----

    [Fact]
    public async Task SearchReturnsSlugsWithTheHitTitleAsAnAlt()
    {
        var handler = new Handler().On(HttpMethod.Post, "/series/search", $$"""
            {"total_hits":2,"page":1,"per_page":6,"results":[
              {"record":{"series_id":{{OnePieceId}},"title":"One Piece","url":"https://www.mangaupdates.com/series/pb8uwds/one-piece"},
               "hit_title":"Wan Pisu"},
              {"record":{"title":"Only A Url &amp; Title","url":"https://www.mangaupdates.com/series/abc12/x"},"hit_title":"Only A Url & Title"}]}
            """);

        var results = await (Build(handler)).Tracker.SearchAsync(UserId, "One Piece");

        Assert.Equal([OnePiece, "abc12"], results.Select(r => r.Id));
        Assert.Equal(["Wan Pisu"], results[0].AltTitles);
        Assert.Equal("https://www.mangaupdates.com/series/pb8uwds", results[0].Url);
        Assert.Equal("Only A Url & Title", results[1].Title);
        Assert.Empty(results[1].AltTitles);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"search\":\"One Piece\"", request.Body);
        Assert.Null(request.Bearer);
    }

    [Fact]
    public async Task ACloudflareChallengeBacksTheTrackerOff()
    {
        var handler = new Handler().On(HttpMethod.Get, $"/lists/series/{OnePieceId}", _ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><title>Just a moment...</title></html>", Encoding.UTF8, "text/html"),
            });
        var tracker = (Build(handler)).Tracker;

        await Assert.ThrowsAnyAsync<TrackerException>(() => tracker.GetEntryAsync(UserId, OnePiece));
        var after = handler.Requests.Count;

        Assert.False(await tracker.AuthenticatedAsync(UserId));
        await Assert.ThrowsAnyAsync<TrackerException>(() => tracker.GetEntryAsync(UserId, OnePiece));
        Assert.Equal(after, handler.Requests.Count);
    }
}
