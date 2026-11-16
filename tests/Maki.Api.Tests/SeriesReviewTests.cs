using System.Net;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class SeriesReviewTests
{
    private const string AniListPage = """
        {"data":{"Media":{"reviews":{"nodes":[
          {"summary":" A masterpiece ","body":"# ~~~__Great__~~~\n\nThe art is _stunning_. ~!Griffith wins!~\n\nimg220(https://x/y.jpg)\n---\nRead [the manga](https://anilist.co).","score":95,"rating":701,"createdAt":1621621060,"siteUrl":"https://anilist.co/review/1","user":{"name":"c4e"}},
          {"summary":"All spoilers","body":"~!everything!~","score":80,"rating":5,"createdAt":1621621060,"siteUrl":"https://anilist.co/review/2","user":{"name":"x"}},
          {"summary":"Fine","body":"Pretty good.","score":0,"rating":2,"createdAt":1621621060,"siteUrl":"https://anilist.co/review/3","user":null}
        ]}}}}
        """;

    private const string KitsuPage = """
        {"data":[
          {"id":"1","type":"mediaReactions","attributes":{"createdAt":"2017-07-25T09:40:43.455Z","reaction":"Held one","upVotesCount":200,"heldReason":"spam"},
           "relationships":{"user":{"data":{"type":"users","id":"10"}},"libraryEntry":{"data":{"type":"libraryEntries","id":"20"}}}},
          {"id":"2","type":"mediaReactions","attributes":{"createdAt":"2017-07-25T09:40:43.455Z","reaction":"The definition of dark fantasy.","upVotesCount":98,"heldReason":null},
           "relationships":{"user":{"data":{"type":"users","id":"10"}},"libraryEntry":{"data":{"type":"libraryEntries","id":"20"}}}},
          {"id":"3","type":"mediaReactions","attributes":{"createdAt":"2017-07-29T00:56:16.598Z","reaction":"Eternal hiatus suffering.","upVotesCount":65,"heldReason":null},
           "relationships":{"user":{"data":{"type":"users","id":"11"}},"libraryEntry":{"data":null}}},
          {"id":"4","type":"mediaReactions","attributes":{"createdAt":"2017-07-29T00:56:16.598Z","reaction":"Third take.","upVotesCount":3,"heldReason":null},
           "relationships":{}}
        ],
        "included":[
          {"id":"10","type":"users","attributes":{"name":"Josh"}},
          {"id":"11","type":"users","attributes":{"name":"Reposer"}},
          {"id":"20","type":"libraryEntries","attributes":{"ratingTwenty":17}}
        ]}
        """;

    [Fact]
    public void AniList_markdown_is_reduced_to_prose_without_spoilers_or_media()
    {
        var text = AniListReviewClient.CleanBody(
            "# ~~~__Great__~~~\n\nThe art is _stunning_ and **bold**. ~!Griffith\nwins!~\n\nimg220(https://x/y.jpg)\n<br>\n---\n> Quoted [link](https://a.b) &amp; more");

        Assert.Equal("Great\n\nThe art is stunning and bold.\n\nQuoted link & more", text);
    }

    [Fact]
    public void AniList_keeps_underscores_inside_words()
    {
        Assert.Equal("snake_case_name stays", AniListReviewClient.CleanBody("snake_case_name stays"));
    }

    [Fact]
    public void AniList_reviews_carry_a_ten_point_score_headline_and_upvotes()
    {
        var reviews = AniListReviewClient.ParseReviews(AniListPage);

        // The all-spoiler review cleans down to nothing and is skipped.
        Assert.Equal(2, reviews.Count);
        var first = reviews[0];
        Assert.Equal("c4e", first.Author);
        Assert.Equal(9.5, first.Score);
        Assert.Equal("A masterpiece", first.Title);
        Assert.Equal(701, first.Likes);
        Assert.Equal("2021-05-21", first.Date);
        Assert.Equal(ReviewSource.AniList, first.Source);
        Assert.DoesNotContain("Griffith", first.Text);
        Assert.DoesNotContain("img", first.Text);
        Assert.StartsWith("Great", first.Text);

        Assert.Null(reviews[1].Author);
        Assert.Null(reviews[1].Score);
    }

    [Fact]
    public void AniList_drops_a_body_heading_that_repeats_the_headline()
    {
        const string page = """
            {"data":{"Media":{"reviews":{"nodes":[
              {"summary":"A Timeless Masterpiece","body":"# ~~~___A Timeless Masterpiece___~~~\n\n___\n\nThe first part is spoiler free.","score":100,"rating":1,"createdAt":1621621060,"siteUrl":"u","user":{"name":"c4e"}}
            ]}}}}
            """;

        Assert.Equal("The first part is spoiler free.", AniListReviewClient.ParseReviews(page).Single().Text);
    }

    [Fact]
    public async Task AniList_looks_up_by_mal_id_and_treats_an_unknown_series_as_unreviewed()
    {
        string? sentBody = null;
        using var handler = new StubHandler(async request =>
        {
            sentBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"errors":[{"message":"Not Found.","status":404}],"data":{"Media":null}}"""),
            };
        });
        var client = new AniListReviewClient(Factory(handler, "https://graphql.anilist.co/"), NullLogger<AniListReviewClient>.Instance);

        var reviews = await client.GetReviewsAsync(null, 2);

        Assert.NotNull(reviews);
        Assert.Empty(reviews);
        Assert.Contains("\"idMal\":2", sentBody);
        Assert.DoesNotContain("\"id\":", sentBody);
    }

    [Fact]
    public async Task AniList_rate_limit_is_a_failure_and_is_not_cached()
    {
        var calls = 0;
        using var handler = new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") });
        });
        var client = new AniListReviewClient(Factory(handler, "https://graphql.anilist.co/"), NullLogger<AniListReviewClient>.Instance);

        Assert.Null(await client.GetReviewsAsync(30002, null));
        Assert.Null(await client.GetReviewsAsync(30002, null));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Kitsu_takes_the_two_most_liked_unheld_reactions_with_the_readers_score()
    {
        var reactions = KitsuReactionClient.ParseReactions(KitsuPage);

        Assert.Equal(["The definition of dark fantasy.", "Eternal hiatus suffering."], reactions.Select(r => r.Text));
        Assert.Equal("Josh", reactions[0].Author);
        Assert.Equal(8.5, reactions[0].Score);
        Assert.Equal(98, reactions[0].Likes);
        Assert.Equal("2017-07-25", reactions[0].Date);
        Assert.Equal(ReviewSource.Kitsu, reactions[0].Source);
        // A hidden library entry leaves the take without a score rather than dropping it.
        Assert.Equal("Reposer", reactions[1].Author);
        Assert.Null(reactions[1].Score);
    }

    [Fact]
    public void Reviews_alternate_between_sites()
    {
        static MangaReview R(string text, string source) => new(null, null, text, null, null, [], source);

        var merged = SeriesReviewService.Interleave(
            [R("mal1", ReviewSource.MyAnimeList), R("mal2", ReviewSource.MyAnimeList), R("mal3", ReviewSource.MyAnimeList)],
            [R("al1", ReviewSource.AniList)]);

        Assert.Equal(["mal1", "al1", "mal2", "mal3"], merged.Select(r => r.Text));
    }

    [Fact]
    public async Task A_failed_site_is_named_and_the_others_still_answer()
    {
        using var handler = new StubHandler(request => Task.FromResult(request.RequestUri!.Host switch
        {
            "graphql.anilist.co" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AniListPage) },
            "kitsu.app" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") },
        }));
        var factory = new RoutingFactory(handler);
        var service = new SeriesReviewService(
            new MalReviewClient(factory, NullLogger<MalReviewClient>.Instance),
            new AniListReviewClient(factory, NullLogger<AniListReviewClient>.Instance),
            new KitsuReactionClient(factory, NullLogger<KitsuReactionClient>.Instance));

        var result = await service.GetAsync(malId: 2, aniListId: 30002, kitsuId: 8);

        Assert.Equal([ReviewSource.Kitsu], result.Unavailable);
        Assert.Equal(2, result.Reviews.Count);
        Assert.All(result.Reviews, r => Assert.Equal(ReviewSource.AniList, r.Source));
        Assert.Empty(result.QuickTakes);
    }

    [Fact]
    public async Task No_ids_asks_nobody()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));
        var factory = new RoutingFactory(handler);
        var service = new SeriesReviewService(
            new MalReviewClient(factory, NullLogger<MalReviewClient>.Instance),
            new AniListReviewClient(factory, NullLogger<AniListReviewClient>.Instance),
            new KitsuReactionClient(factory, NullLogger<KitsuReactionClient>.Instance));

        var result = await service.GetAsync(null, null, null);

        Assert.Empty(result.Reviews);
        Assert.Empty(result.QuickTakes);
        Assert.Empty(result.Unavailable);
    }

    private static IHttpClientFactory Factory(HttpMessageHandler handler, string baseAddress) =>
        new SingleFactory(new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(baseAddress) });

    private sealed class SingleFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>One handler behind each named client's real base address.</summary>
    private sealed class RoutingFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(name switch
            {
                AniListReviewClient.HttpClientName => "https://graphql.anilist.co/",
                KitsuReactionClient.HttpClientName => "https://kitsu.app/api/edge/",
                _ => "https://myanimelist.net/",
            }),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
