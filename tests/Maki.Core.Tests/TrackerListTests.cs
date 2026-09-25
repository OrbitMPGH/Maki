using System.Net;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

/// <summary>
/// <see cref="IScrobbleTracker.ListAsync"/> on all four trackers against canned responses: pages
/// stitched to the end, the status filter, the cross ids each site hands out, and a 401 surfacing as
/// a <see cref="TrackerException"/>.
/// </summary>
public class TrackerListTests
{
    private const int UserId = 1;

    private static readonly ScrobbleTrackerOptions Options = new(
        "https://anilist.test/graphql", "https://anilist.test/oauth",
        "https://mal.test", "https://mal.test/oauth",
        "https://mangabaka.test",
        "https://kitsu.test/api/edge", "https://kitsu.test/api/oauth");

    /// <summary>Answers from the first rule whose predicate matches the URL and request body.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private readonly List<(Func<string, string, bool> Match, HttpStatusCode Status, string Json)> _rules = [];

        public List<(string Url, string Body)> Requests { get; } = [];

        public Handler On(Func<string, string, bool> match, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _rules.Add((match, status, json));
            return this;
        }

        public Handler OnUrl(string contains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
            On((url, _) => url.Contains(contains, StringComparison.Ordinal), json, status);

        public Handler Always(HttpStatusCode status, string json) => On((_, _) => true, json, status);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((url, body));
            foreach (var (match, status, json) in _rules)
            {
                if (match(url, body))
                {
                    return new HttpResponseMessage(status)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"message":"unmatched"}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings(Dictionary<string, string>? values = null) : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(values?.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UserSettings(Dictionary<string, string> values) : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TokenStore(string service) : IScrobbleTokenStore
    {
        public ScrobbleToken? Token { get; set; } = new()
        {
            UserId = UserId,
            Service = service,
            AccessToken = "access",
            ExpiresAt = DateTime.UtcNow.AddDays(20),
        };

        public Task<ScrobbleToken?> GetAsync(int userId, string s, CancellationToken ct = default) => Task.FromResult(Token);

        public Task SaveAsync(ScrobbleToken token, CancellationToken ct = default)
        {
            Token = token;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int userId, string s, CancellationToken ct = default)
        {
            Token = null;
            return Task.CompletedTask;
        }
    }

    private static readonly ScrobbleStatus[] ReadingAndPlanned = [ScrobbleStatus.Reading, ScrobbleStatus.PlanToRead];

    // ---- AniList ----

    private static AniListTracker AniList(Handler handler) => new(
        new Factory(handler), new Settings(), new TokenStore("anilist"), Options, NullLogger<AniListTracker>.Instance);

    [Fact]
    public async Task AniList_walks_chunks_and_filters_statuses()
    {
        const string chunk1 = """
            {"data":{"MediaListCollection":{"hasNextChunk":true,"lists":[
              {"entries":[{"status":"CURRENT","media":{"id":30002,"idMal":2,"title":{"romaji":"Berserk","english":null}}}]},
              {"entries":[{"status":"COMPLETED","media":{"id":30013,"idMal":13,"title":{"romaji":"One Piece"}}}]}]}}}
            """;
        const string chunk2 = """
            {"data":{"MediaListCollection":{"hasNextChunk":false,"lists":[
              {"entries":[{"status":"PLANNING","media":{"id":85737,"idMal":null,"title":{"romaji":"Kaguya","english":"Love Is War"}}}]}]}}}
            """;
        var handler = new Handler()
            .On((_, body) => body.Contains("Viewer"), """{"data":{"Viewer":{"id":77}}}""")
            .On((_, body) => body.Contains("\"chunk\":1"), chunk1)
            .On((_, body) => body.Contains("\"chunk\":2"), chunk2);

        var list = await AniList(handler).ListAsync(UserId, ReadingAndPlanned);

        Assert.Equal(["30002", "85737"], list.Select(e => e.RemoteId));
        var berserk = list[0];
        Assert.Equal((ScrobbleStatus.Reading, "Berserk", 30002L, 2L), (berserk.Status, berserk.Title, berserk.AniListId, berserk.MalId));
        Assert.Equal((ScrobbleStatus.PlanToRead, "Love Is War", (long?)null), (list[1].Status, list[1].Title, list[1].MalId));

        var first = handler.Requests.Single(r => r.Body.Contains("\"chunk\":1")).Body;
        Assert.Contains("\"userId\":77", first);
        Assert.Contains("\"statuses\":[\"CURRENT\",\"REPEATING\",\"PLANNING\"]", first);
    }

    [Fact]
    public async Task AniList_401_is_a_tracker_exception()
    {
        var handler = new Handler().Always(HttpStatusCode.Unauthorized, """{"errors":[{"message":"Invalid token"}]}""");

        await Assert.ThrowsAsync<TrackerException>(() => AniList(handler).ListAsync(UserId, ReadingAndPlanned));
    }

    /// <summary>
    /// The anime list carries format, air dates, episode count and progress. A date missing its day
    /// is null, never a guessed first of the month.
    /// </summary>
    [Fact]
    public async Task AniList_anime_list_reads_format_dates_episodes_and_progress()
    {
        const string page = """
            {"data":{"Page":{"pageInfo":{"hasNextPage":false},"mediaList":[
              {"score":9,"status":"COMPLETED","progress":25,
               "media":{"id":16498,"idMal":16498,"title":{"romaji":"Shingeki no Kyojin","english":"Attack on Titan"},
                        "format":"TV","episodes":25,
                        "startDate":{"year":2013,"month":4,"day":7},"endDate":{"year":2013,"month":9,"day":29},
                        "relations":{"edges":[]}}},
              {"score":0,"status":"CURRENT","progress":3,
               "media":{"id":1,"idMal":null,"title":{"romaji":"Airing"},
                        "format":"ONA","episodes":null,
                        "startDate":{"year":2026,"month":7,"day":null},"endDate":{"year":null,"month":null,"day":null},
                        "relations":{"edges":[]}}}]}}}
            """;
        var handler = new Handler()
            .On((_, body) => body.Contains("Viewer"), """{"data":{"Viewer":{"id":77}}}""")
            .On((_, body) => body.Contains("mediaList"), page);

        var list = await AniList(handler).ListAnimeAsync(UserId);

        Assert.Equal(2, list.Count);
        var aot = list[0];
        Assert.Equal(("TV", 25, 25), (aot.Format, aot.Episodes, aot.Progress));
        Assert.Equal(new DateOnly(2013, 4, 7), aot.StartDate);
        Assert.Equal(new DateOnly(2013, 9, 29), aot.EndDate);
        var airing = list[1];
        Assert.Equal(("ONA", (int?)null, 3), (airing.Format, airing.Episodes, airing.Progress));
        Assert.Null(airing.StartDate);
        Assert.Null(airing.EndDate);
        Assert.Null(airing.Score);
    }

    // ---- MyAnimeList ----

    private static MalTracker Mal(Handler handler) => new(
        new Factory(handler), new Settings(), new TokenStore("mal"), Options, NullLogger<MalTracker>.Instance);

    [Fact]
    public async Task Mal_pages_each_status_and_filters()
    {
        var page1 = "{\"data\":[" + string.Join(",", Enumerable.Range(1, 1000).Select(i =>
            $$$"""{"node":{"id":{{{i}}},"title":"Manga {{{i}}}"},"list_status":{"status":"reading"}}""")) +
            """],"paging":{"next":"https://mal.test/users/@me/mangalist?offset=1000"}}""";
        const string page2 = """
            {"data":[{"node":{"id":5000,"title":"Last"},"list_status":{"status":"reading"}},
                     {"node":{"id":5001,"title":"Stray"},"list_status":{"status":"dropped"}}],"paging":{}}
            """;
        const string planned = """
            {"data":[{"node":{"id":6000,"title":"Later"},"list_status":{"status":"plan_to_read"}}],"paging":{}}
            """;
        var handler = new Handler()
            .OnUrl("status=reading&fields=list_status&nsfw=true&limit=1000&offset=0", page1)
            .OnUrl("status=reading&fields=list_status&nsfw=true&limit=1000&offset=1000", page2)
            .OnUrl("status=plan_to_read", planned);

        var list = await Mal(handler).ListAsync(UserId, ReadingAndPlanned);

        Assert.Equal(1002, list.Count);
        Assert.Equal(1000, list.Count(e => e.Status == ScrobbleStatus.Reading && e.MalId <= 1000));
        Assert.Contains(list, e => e is { RemoteId: "5000", MalId: 5000, Title: "Last", Status: ScrobbleStatus.Reading });
        Assert.Contains(list, e => e is { RemoteId: "6000", Status: ScrobbleStatus.PlanToRead });
        Assert.DoesNotContain(list, e => e.RemoteId == "5001");
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("status=completed"));
    }

    [Fact]
    public async Task Mal_401_is_a_tracker_exception()
    {
        var handler = new Handler().Always(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""");

        await Assert.ThrowsAsync<TrackerException>(() => Mal(handler).ListAsync(UserId, ReadingAndPlanned));
    }

    /// <summary>
    /// MAL names formats in lower case and dates as YYYY, YYYY-MM or YYYY-MM-DD. Only the full form
    /// becomes a date, "unknown" is no format, and zero episodes means the count is not known.
    /// </summary>
    [Fact]
    public async Task Mal_anime_list_reads_format_dates_episodes_and_progress()
    {
        const string page = """
            {"data":[
              {"node":{"id":16498,"title":"Shingeki no Kyojin","media_type":"tv","num_episodes":25,
                       "start_date":"2013-04-07","end_date":"2013-09-29"},
               "list_status":{"status":"completed","score":9,"num_episodes_watched":25}},
              {"node":{"id":2,"title":"Film","media_type":"movie","num_episodes":1,"start_date":"2015-08"},
               "list_status":{"status":"watching","score":0,"num_episodes_watched":0}},
              {"node":{"id":3,"title":"Mystery","media_type":"unknown","num_episodes":0,"start_date":"2027"},
               "list_status":{"status":"plan_to_watch","score":0,"num_episodes_watched":0}},
              {"node":{"id":4,"title":"Short","media_type":"tv_short","num_episodes":12}}],"paging":{}}
            """;
        var handler = new Handler()
            .OnUrl("/users/@me/animelist?fields=list_status,media_type,num_episodes,start_date,end_date&nsfw=true", page);

        var list = await Mal(handler).ListAnimeAsync(UserId);

        Assert.Equal(4, list.Count);
        var aot = list[0];
        Assert.Equal(("TV", 25, 25, 9), (aot.Format, aot.Episodes, aot.Progress, aot.Score));
        Assert.Equal(new DateOnly(2013, 4, 7), aot.StartDate);
        Assert.Equal(new DateOnly(2013, 9, 29), aot.EndDate);
        var film = list[1];
        Assert.Equal(("MOVIE", 1, 0), (film.Format, film.Episodes, film.Progress));
        Assert.Null(film.StartDate);
        Assert.Null(film.EndDate);
        var mystery = list[2];
        Assert.Null(mystery.Format);
        Assert.Null(mystery.Episodes);
        Assert.Null(mystery.StartDate);
        var shortOne = list[3];
        Assert.Equal("TV_SHORT", shortOne.Format);
        Assert.Null(shortOne.Progress);
    }

    // ---- Kitsu ----

    private static KitsuTracker Kitsu(Handler handler) => new(
        new Factory(handler),
        new Settings(new Dictionary<string, string>
        {
            [SettingKeys.ScrobbleKitsuClientId] = "client",
            [SettingKeys.ScrobbleKitsuClientSecret] = "secret",
        }),
        new UserSettings(new Dictionary<string, string>
        {
            [SettingKeys.ScrobbleKitsuEmail] = "reader@example.test",
            [SettingKeys.ScrobbleKitsuPassword] = "pw",
        }),
        new TokenStore("kitsu"),
        Options,
        NullLogger<KitsuTracker>.Instance);

    private const string KitsuProfile = """{"data":[{"id":"42603","type":"users","attributes":{"name":"reader"}}]}""";

    [Fact]
    public async Task Kitsu_pages_by_offset_and_reads_mappings()
    {
        const string page1 = """
            {"data":[{"id":"1","type":"libraryEntries","attributes":{"status":"current"},
                      "relationships":{"manga":{"data":{"type":"manga","id":"14916"}}}},
                     {"id":"2","type":"libraryEntries","attributes":{"status":"on_hold"},
                      "relationships":{"manga":{"data":{"type":"manga","id":"99"}}}}],
             "included":[{"id":"14916","type":"manga","attributes":{"canonicalTitle":"Attack on Titan"},
                          "relationships":{"mappings":{"data":[{"type":"mappings","id":"30080"},{"type":"mappings","id":"256037"},{"type":"mappings","id":"1"}]}}},
                         {"id":"30080","type":"mappings","attributes":{"externalSite":"myanimelist/manga","externalId":"23390"}},
                         {"id":"256037","type":"mappings","attributes":{"externalSite":"anilist/manga","externalId":"53390"}},
                         {"id":"1","type":"mappings","attributes":{"externalSite":"mangaupdates","externalId":"47446"}}],
             "links":{"next":"https://kitsu.test/api/edge/library-entries?page%5Boffset%5D=500"}}
            """;
        const string page2 = """
            {"data":[{"id":"3","type":"libraryEntries","attributes":{"status":"planned"},
                      "relationships":{"manga":{"data":{"type":"manga","id":"23815"}}}}],
             "included":[{"id":"23815","type":"manga","attributes":{"canonicalTitle":"Noblesse"},
                          "relationships":{"mappings":{"data":[]}}}],
             "links":{}}
            """;
        var handler = new Handler()
            .OnUrl("/users?filter[self]=true", KitsuProfile)
            .OnUrl("page[offset]=0", page1)
            .OnUrl("page[offset]=500", page2);

        var list = await Kitsu(handler).ListAsync(UserId, ReadingAndPlanned);

        Assert.Equal(["14916", "23815"], list.Select(e => e.RemoteId));
        Assert.Equal(
            (ScrobbleStatus.Reading, "Attack on Titan", 14916L, 23390L, 53390L),
            (list[0].Status, list[0].Title, list[0].KitsuId, list[0].MalId, list[0].AniListId));
        Assert.Equal((ScrobbleStatus.PlanToRead, "Noblesse", (long?)null), (list[1].Status, list[1].Title, list[1].MalId));

        var first = handler.Requests.First(r => r.Url.Contains("page[offset]=0")).Url;
        Assert.Contains("filter[userId]=42603", first);
        Assert.Contains("filter[status]=current,planned", first);
        Assert.Contains("include=manga,manga.mappings", first);
    }

    [Fact]
    public async Task Kitsu_401_is_a_tracker_exception()
    {
        var handler = new Handler().Always(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""");

        await Assert.ThrowsAsync<TrackerException>(() => Kitsu(handler).ListAsync(UserId, ReadingAndPlanned));
    }

    // ---- MangaBaka ----

    private static MangaBakaTracker MangaBaka(Handler handler) => new(
        new Factory(handler),
        new UserSettings(new Dictionary<string, string> { [SettingKeys.ScrobbleMangaBakaToken] = "pat" }),
        new TokenStore("mangabaka"),
        Options,
        NullLogger<MangaBakaTracker>.Instance);

    [Fact]
    public async Task MangaBaka_pages_v2_library_and_reads_source_ids()
    {
        const string reading1 = """
            {"status":200,"pagination":{"count":2,"page":1,"limit":100,"next":"https://mangabaka.test/v2/my/library?page=2","previous":null},
             "data":[{"entry":{"series_id":84926,"state":"reading"},"lists":[],
                      "series":{"id":84926,"title":"Frieren","titles":[],
                                "source":{"anilist":{"id":118586,"rating":null},"my_anime_list":{"id":126287,"rating":null},
                                          "kitsu":{"id":44052,"rating":null},"manga_updates":{"id":"x","rating":null}}}}]}
            """;
        const string rereading1 = """
            {"status":200,"pagination":{"count":0,"page":1,"limit":100,"next":"https://mangabaka.test/v2/my/library?page=2","previous":null},
             "data":[]}
            """;
        const string planned1 = """
            {"status":200,"pagination":{"count":2,"page":1,"limit":100,"next":null,"previous":null},
             "data":[{"entry":{"series_id":7,"state":"plan_to_read"},"lists":[],
                      "series":{"id":7,"title":"Later","source":{"anilist":{"id":null,"rating":null}}}},
                     {"entry":{"series_id":5,"state":"dropped"},"lists":[],"series":{"id":5,"title":"Dropped","source":{}}}]}
            """;
        var handler = new Handler()
            .OnUrl("page=1&state=reading", reading1)
            // Page 2 repeats page 1, as an endpoint ignoring `page` would: no new id ends the pass.
            .OnUrl("page=2&state=reading", reading1)
            .OnUrl("page=1&state=rereading", rereading1)
            .OnUrl("page=1&state=plan_to_read", planned1);

        var list = await MangaBaka(handler).ListAsync(UserId, ReadingAndPlanned);

        Assert.Equal(["84926", "7"], list.Select(e => e.RemoteId));
        Assert.Equal(
            (ScrobbleStatus.Reading, "Frieren", 84926L, 118586L, 126287L, 44052L),
            (list[0].Status, list[0].Title, list[0].MangaBakaId, list[0].AniListId, list[0].MalId, list[0].KitsuId));
        Assert.Equal((ScrobbleStatus.PlanToRead, (long?)null), (list[1].Status, list[1].AniListId));

        Assert.Equal(
            [
                "https://mangabaka.test/v2/my/library?limit=100&page=1&state=reading",
                "https://mangabaka.test/v2/my/library?limit=100&page=2&state=reading",
                "https://mangabaka.test/v2/my/library?limit=100&page=1&state=rereading",
                "https://mangabaka.test/v2/my/library?limit=100&page=1&state=plan_to_read",
            ],
            handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task MangaBaka_401_is_a_tracker_exception()
    {
        var handler = new Handler().Always(HttpStatusCode.Unauthorized, """{"message":"No session found","status":401}""");

        await Assert.ThrowsAsync<TrackerException>(() => MangaBaka(handler).ListAsync(UserId, ReadingAndPlanned));
    }

    // ---- dead remote ids ----

    [Fact]
    public async Task Mal_404_on_an_entry_is_entry_not_found()
    {
        var handler = new Handler().OnUrl("/manga/42", """{"error":"not_found"}""", HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<TrackerEntryNotFoundException>(() => Mal(handler).GetEntryAsync(UserId, "42"));
    }

    [Fact]
    public async Task MangaBaka_404_on_the_library_entry_is_not_entry_not_found()
    {
        var handler = new Handler()
            .OnUrl("/v2/series/42", """{"status":200,"data":{"id":42,"title":"Frieren","total_chapters":"10"}}""")
            .OnUrl("/v1/my/library/42", """{"status":404,"message":"not in library"}""", HttpStatusCode.NotFound);

        var entry = await MangaBaka(handler).GetEntryAsync(UserId, "42");

        Assert.Null(entry.Status);
    }
}
