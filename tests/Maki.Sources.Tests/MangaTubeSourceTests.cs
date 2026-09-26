using System.Net;
using System.Text;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.MangaTube;

namespace Maki.Sources.Tests;

public class MangaTubeSourceTests
{
    private static MangaTubeSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task Search_maps_hits_to_series_results()
    {
        var source = SourceFor(new()
        {
            ["quick-search"] = FakeHttpClientFactory.Fixture("mangatube-search.json")
        });

        var results = await source.SearchAsync("one piece");

        Assert.Equal(9, results.Count);
        Assert.Contains(results, r => r.SourceSeriesId == "one_piece" && r.Title == "One Piece");
        var onePiece = results.Single(r => r.SourceSeriesId == "one_piece");
        Assert.Equal("https://manga-tube.me/series/one_piece", onePiece.Url);
        Assert.NotNull(onePiece.CoverUrl);
    }

    [Fact]
    public async Task GetSeries_reads_title_status_and_cover()
    {
        var source = SourceFor(new()
        {
            ["api/manga/one_piece"] = FakeHttpClientFactory.Fixture("mangatube-series.json")
        });

        var detail = await source.GetSeriesAsync("one_piece");

        Assert.Equal("One Piece", detail.Title);
        Assert.Equal("https://manga-tube.me/series/one_piece", detail.Url);
        // status: 0 on this (ongoing) title maps to null, not "Ongoing" - only 1/2 are known.
        Assert.Null(detail.Status);
        Assert.NotNull(detail.CoverUrl);
        Assert.NotNull(detail.Description);
    }

    [Fact]
    public async Task ListChapters_parses_numbers_language_and_dates_and_normalizes_ascending()
    {
        var source = SourceFor(new()
        {
            ["one_piece/chapters"] = FakeHttpClientFactory.Fixture("mangatube-chapters.json")
        });

        var chapters = await source.ListChaptersAsync("one_piece");

        Assert.Equal(5, chapters.Count);
        Assert.Equal([1189m, 1190m, 1191m, 1192m, 1193m], chapters.Select(c => c.Number));
        Assert.All(chapters, c => Assert.Equal("de", c.Language));
        Assert.All(chapters, c => Assert.Equal("mangatube", c.SourceName));
        Assert.All(chapters, c => Assert.NotNull(c.Title));

        var first = chapters.Single(c => c.Number == 1193m);
        Assert.Equal("19830", first.SourceChapterId);
        Assert.Equal("Ich arbeite noch daran", first.Title);
        Assert.Equal("https://manga-tube.me/series/one_piece/read/19830", first.Url);
        // publishedAt "2026-09-13 12:53:06" has no timezone; treated as Europe/Berlin (CEST,
        // UTC+2 in September) and converted to UTC.
        Assert.Equal(new DateTime(2026, 9, 13, 10, 53, 6, DateTimeKind.Utc), first.ReleaseDate);
    }

    [Fact]
    public async Task ListChapters_drops_hidden_and_unpublished_rows_and_handles_subnumbers()
    {
        const string body = """
        {
            "success": true,
            "data": {
                "chapters": [
                    { "id": 1, "number": 5, "subNumber": 0, "volume": 0, "name": "Visible", "hidden": false, "published": true, "publishedAt": "2026-01-01 00:00:00" },
                    { "id": 2, "number": 6, "subNumber": 0, "volume": 0, "name": "Hidden", "hidden": true, "published": true, "publishedAt": "2026-01-01 00:00:00" },
                    { "id": 3, "number": 7, "subNumber": 0, "volume": 0, "name": "Unpublished", "hidden": false, "published": false, "publishedAt": "2026-01-01 00:00:00" },
                    { "id": 4, "number": 5, "subNumber": 1, "volume": 2, "name": "", "hidden": false, "published": true, "publishedAt": "2026-01-01 00:00:00" }
                ]
            }
        }
        """;

        var source = SourceFor(new() { ["edge_series/chapters"] = body });

        var chapters = await source.ListChaptersAsync("edge_series");

        Assert.Equal(2, chapters.Count);
        Assert.DoesNotContain(chapters, c => c.SourceChapterId is "2" or "3");

        var subChapter = chapters.Single(c => c.SourceChapterId == "4");
        Assert.Equal(5.1m, subChapter.Number);
        Assert.Equal(2, subChapter.Volume);
        // A blank name still needs a non-null Title: ChapterIdentity dedupes null-number
        // chapters by Title, and this one does have a number, but an empty name must not
        // leak through as an empty-string title either.
        Assert.Null(subChapter.Title);
    }

    [Fact]
    public async Task ListChapters_treats_a_licence_check_401_as_zero_chapters()
    {
        // FakeHttpClientFactory only ever answers 200, so the 401 status itself (not just the
        // fixture's error body) needs its own handler to prove ListChaptersAsync reacts to it.
        var handler = new StatusCodeHandler(HttpStatusCode.Unauthorized,
            FakeHttpClientFactory.Fixture("mangatube-chapters-licensed.json"));
        var licensedSource = new MangaTubeSource(new SingleHandlerFactory(handler));

        var chapters = await licensedSource.ListChaptersAsync("solo_leveling");

        Assert.Empty(chapters);
    }

    [Fact]
    public async Task ListChapters_throws_on_an_error_status_with_a_json_body()
    {
        var handler = new StatusCodeHandler(HttpStatusCode.InternalServerError, """{"success":false,"error":["server-error"]}""");
        var source = new MangaTubeSource(new SingleHandlerFactory(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.ListChaptersAsync("one_piece"));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task ListChapters_surfaces_a_429_as_a_rate_limit()
    {
        var handler = new StatusCodeHandler(HttpStatusCode.TooManyRequests, """{"success":false,"error":["too-many-requests"]}""");
        var source = new MangaTubeSource(new SingleHandlerFactory(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.ListChaptersAsync("one_piece"));
        Assert.True(RateLimitDetector.IsRateLimit(ex, out _));
    }

    [Fact]
    public async Task ListChapters_throws_when_data_chapters_is_missing()
    {
        var source = SourceFor(new() { ["one_piece/chapters"] = """{"success":true,"data":{}}""" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("one_piece"));
    }

    [Fact]
    public async Task GetSeries_and_Search_throw_on_a_rate_limit_status()
    {
        var handler = new StatusCodeHandler(HttpStatusCode.ServiceUnavailable, """{"success":false}""");
        var source = new MangaTubeSource(new SingleHandlerFactory(handler));

        var seriesEx = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetSeriesAsync("one_piece"));
        var searchEx = await Assert.ThrowsAsync<HttpRequestException>(() => source.SearchAsync("one piece"));
        Assert.True(RateLimitDetector.IsRateLimit(seriesEx, out _));
        Assert.True(RateLimitDetector.IsRateLimit(searchEx, out _));
    }

    [Fact]
    public async Task GetPages_throws_a_rate_limit_rather_than_ChapterLocked_on_429()
    {
        var handler = new StatusCodeHandler(HttpStatusCode.TooManyRequests, """{"success":false}""");
        var source = new MangaTubeSource(new SingleHandlerFactory(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetPagesAsync(
            new SourceChapter("mangatube", "one_piece", "19830", "1193", 1193m, null, "Ch. 1193", "de", null)));
        Assert.True(RateLimitDetector.IsRateLimit(ex, out _));
    }

    [Fact]
    public async Task GetPages_orders_by_page_number_and_sets_referer()
    {
        var source = SourceFor(new()
        {
            ["one_piece/chapter/19830"] = FakeHttpClientFactory.Fixture("mangatube-pages.json")
        });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "mangatube", "one_piece", "19830", "1193", 1193m, null, "Ich arbeite noch daran", "de", null));

        Assert.Equal(16, pages.Pages.Count);
        Assert.Equal(
            "https://a.mtcdn.org/m/4-one_piece/7cc60240-7d4f-4ad4-a808-ecabd3805b0f/processed/001_Q6NCQI.png",
            pages.Pages[0].Url);
        Assert.Equal(
            "https://a.mtcdn.org/m/4-one_piece/7cc60240-7d4f-4ad4-a808-ecabd3805b0f/processed/016_vaBf7B.png",
            pages.Pages[^1].Url);
        Assert.All(pages.Pages, p => Assert.Equal("https://manga-tube.me/", p.Headers!["Referer"]));
    }

    [Fact]
    public async Task GetPages_throws_ChapterLocked_when_the_chapter_is_licence_restricted()
    {
        var handler = new StatusCodeHandler(HttpStatusCode.Unauthorized, """{"success":false,"error":["licence-check"]}""");
        var source = new MangaTubeSource(new SingleHandlerFactory(handler));

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(
            new SourceChapter("mangatube", "solo_leveling", "1", "1", 1m, null, "Ch. 1", "de", null)));
    }

    [Fact]
    public async Task GetPages_throws_ChapterLocked_instead_of_returning_an_empty_list()
    {
        const string body = """{"success":true,"data":{"chapter":{"pages":[]}}}""";
        var source = SourceFor(new() { ["chapter/1"] = body });

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(
            new SourceChapter("mangatube", "some_series", "1", "1", 1m, null, "Ch. 1", "de", null)));
    }

    [Theory]
    [InlineData("https://manga-tube.me/series/one_piece", "one_piece")]
    [InlineData("https://manga-tube.me/series/one_piece/", "one_piece")]
    [InlineData("https://manga-tube.me/series/one_piece/read/19830/1", null)]
    [InlineData("https://manga-tube.me/api/manga/one_piece", null)]
    [InlineData("https://example.com/series/one_piece", null)]
    public void ResolveSeriesIdFromUrl_accepts_only_bare_series_pages(string url, string? expected)
    {
        ISource source = SourceFor([]);
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }

    // ── Challenge solver ──────────────────────────────────────────────

    [Fact]
    public async Task Session_solves_the_arithmetic_challenge_once_and_reuses_the_pass()
    {
        var handler = new ChallengeHandler(
            challengeHtml: FakeHttpClientFactory.Fixture("mangatube-challenge.html"),
            realResponsesByPathSubstring: new()
            {
                ["one_piece/chapters"] = FakeHttpClientFactory.Fixture("mangatube-chapters.json"),
                ["api/manga/one_piece"] = FakeHttpClientFactory.Fixture("mangatube-series.json"),
            });
        var source = new MangaTubeSource(new SingleHandlerFactory(handler));

        var detail = await source.GetSeriesAsync("one_piece");
        var chapters = await source.ListChaptersAsync("one_piece");

        Assert.Equal("One Piece", detail.Title);
        Assert.Equal(5, chapters.Count);

        // Solved exactly once even though two different endpoints were called afterwards.
        Assert.Equal(1, handler.ShellFetches);
        Assert.Equal(1, handler.SolveAttempts);
        Assert.True(source.ChallengeState.Solved);

        // mangatube-challenge.html carries arg1=d2, arg2=2ae, arg3=c (subtract):
        // 0xd2 - 0x2ae = 210 - 686 = -476.
        Assert.Equal("-476", handler.LastSolutionSubmitted);
    }

    [Fact]
    public async Task Session_re_solves_when_the_server_rejects_an_unexpired_pass()
    {
        var handler = new ChallengeHandler(
            challengeHtml: FakeHttpClientFactory.Fixture("mangatube-challenge.html"),
            realResponsesByPathSubstring: new()
            {
                ["api/manga/one_piece"] = FakeHttpClientFactory.Fixture("mangatube-series.json"),
            });
        var source = new MangaTubeSource(new SingleHandlerFactory(handler));

        await source.GetSeriesAsync("one_piece");
        handler.RevokeCurrentPass();
        var detail = await source.GetSeriesAsync("one_piece");

        Assert.Equal("One Piece", detail.Title);
        Assert.Equal(2, handler.SolveAttempts);
    }

    [Fact]
    public void Solve_divides_and_formats_like_JavaScript_Number_toString()
    {
        // Sample values from the site's own challenge script.
        var solution = MangaTubeSession.Solve(new MangaTubeChallenge("ea", "1c7", "a", "token"));
        Assert.Equal("0.5142857142857142", solution);
    }

    [Fact]
    public void Solve_multiplies_and_formats_integral_results_without_a_decimal_point()
    {
        var solution = MangaTubeSession.Solve(new MangaTubeChallenge("4f", "379", "b", "token"));
        Assert.Equal("70231", solution);
    }

    [Fact]
    public void Solve_subtracts()
    {
        var solution = MangaTubeSession.Solve(new MangaTubeChallenge("d2", "2ae", "c", "token"));
        Assert.Equal("-476", solution);
    }

    [Fact]
    public void Solve_adds()
    {
        var solution = MangaTubeSession.Solve(new MangaTubeChallenge("d2", "2ae", "d", "token"));
        Assert.Equal("896", solution);
    }

    [Fact]
    public void ParseChallenge_reads_the_shell_page_markers()
    {
        var challenge = MangaTubeSession.ParseChallenge(FakeHttpClientFactory.Fixture("mangatube-challenge.html"));

        Assert.Equal("d2", challenge.Arg1);
        Assert.Equal("2ae", challenge.Arg2);
        Assert.Equal("c", challenge.Arg3);
        Assert.Equal(64, challenge.Token.Length);
    }

    /// <summary>Answers every request with a fixed status/body, for a series that is fully
    /// licence-blocked (both the chapters call and the page call 401 the same way).</summary>
    private sealed class StatusCodeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// Models the real site's handshake: any request without the solved cookie gets the
    /// challenge shell, "GET /" serves the shell explicitly, "POST /" grades the solve and
    /// sets a fresh pass cookie, and only a request carrying the latest, unrevoked cookie
    /// reaches the real fixture keyed by a path substring.
    /// </summary>
    private sealed class ChallengeHandler(
        string challengeHtml, Dictionary<string, string> realResponsesByPathSubstring) : HttpMessageHandler
    {
        private string? _passCookie;

        public int ShellFetches { get; private set; }
        public int SolveAttempts { get; private set; }
        public string? LastSolutionSubmitted { get; private set; }

        /// <summary>The server drops the current pass even though its stated expiry is years away.</summary>
        public void RevokeCurrentPass() => _passCookie = null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == "/")
            {
                SolveAttempts++;
                LastSolutionSubmitted = request.Headers.GetValues("x-challange-arg4").Single();
                _passCookie = $"__mtbpass=4102444800000:solved-{SolveAttempts}";
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"responseCode":0}""", Encoding.UTF8, "application/json")
                };
                response.Headers.TryAddWithoutValidation("Set-Cookie", $"{_passCookie}; HttpOnly; path=/");
                return Task.FromResult(response);
            }

            if (request.Method == HttpMethod.Get && path == "/")
            {
                ShellFetches++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(challengeHtml)
                });
            }

            var hasPass = _passCookie is not null &&
                          request.Headers.TryGetValues("Cookie", out var cookies) &&
                          cookies.Any(c => c.Contains(_passCookie, StringComparison.Ordinal));
            if (!hasPass)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(challengeHtml)
                });
            }

            foreach (var (substring, body) in realResponsesByPathSubstring)
            {
                if (path.Contains(substring, StringComparison.OrdinalIgnoreCase) ||
                    request.RequestUri!.ToString().Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fixture for {path}")
            });
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://manga-tube.me/") };
    }
}
