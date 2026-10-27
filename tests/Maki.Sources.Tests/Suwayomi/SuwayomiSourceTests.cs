using System.Net;
using System.Text;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.Suwayomi;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Sources.Tests.Suwayomi;

public class SuwayomiSourceTests
{
    private const string SeriesUrl = "/manga/ippo";

    [Fact]
    public async Task Search_returns_portable_ids_and_no_url_when_the_site_page_is_unknown()
    {
        var h = new SuwayomiHarness();
        h.Server.Ops["Search"] = (vars, request) =>
        {
            Assert.Equal("100", vars.GetProperty("source").GetString());
            Assert.Equal("Hajime no Ippo", vars.GetProperty("query").GetString());
            Assert.Equal("/suwayomi/api/graphql", request.RequestUri!.AbsolutePath);
            return FakeSuwayomi.Data(new
            {
                fetchSourceManga = new
                {
                    mangas = new[]
                    {
                        SuwayomiHarness.Manga(7, SeriesUrl, realUrl: "https://site.test/manga/ippo"),
                        SuwayomiHarness.Manga(8, "/manga/other", "Other", realUrl: null, thumbnailUrl: null)
                    }
                }
            });
        };

        var results = await h.Source().SearchAsync("Hajime no Ippo");

        Assert.Collection(results,
            r =>
            {
                Assert.Equal(SeriesUrl, r.SourceSeriesId);
                Assert.Equal("https://site.test/manga/ippo", r.Url);
                Assert.Equal("/api/v1/suwayomi/thumbnail/100/7", r.CoverUrl);
                Assert.Equal("George Morikawa", r.Author);
            },
            r =>
            {
                Assert.Equal(string.Empty, r.Url);
                Assert.Null(r.CoverUrl);
            });
    }

    [Fact]
    public async Task Chapters_are_mapped_and_duplicates_keep_the_newest_upload()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Refresh(SuwayomiHarness.Manga(7, SeriesUrl),
            SuwayomiHarness.Chapter(1, "/c/15a", "Vol.3 Ch.15 - The Fight", 15, uploadDate: 1_700_000_000_000, sourceOrder: 1, scanlator: "Old Group"),
            SuwayomiHarness.Chapter(2, "/c/15b", "Vol.3 Ch.15 - The Fight", 15, uploadDate: 1_800_000_000_000, sourceOrder: 2, scanlator: " New Group "),
            SuwayomiHarness.Chapter(3, "/c/10.1", "Chapter 10.1", 10.100000381469727, sourceOrder: 3),
            SuwayomiHarness.Chapter(4, "/c/extra", "Extra: Behind the scenes", -1, sourceOrder: 4));

        var chapters = await h.Source().ListChaptersAsync(SeriesUrl);

        Assert.Equal(3, chapters.Count);
        var fifteen = Assert.Single(chapters, c => c.Number == 15m);
        Assert.Equal("/c/15b", fifteen.SourceChapterId);
        Assert.Equal("New Group", fifteen.Group);
        Assert.Equal(3, fifteen.Volume);
        Assert.Equal("The Fight", fifteen.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000).UtcDateTime, fifteen.ReleaseDate);
        Assert.Equal("suwayomi-100", fifteen.SourceName);
        Assert.Equal(SeriesUrl, fifteen.SourceSeriesId);
        Assert.Contains(chapters, c => c.Number == 10.1m && c.ReleaseDate is null);
        var extra = Assert.Single(chapters, c => c.Number is null);
        Assert.Equal("Extra: Behind the scenes", extra.NumberRaw);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
    }

    [Fact]
    public async Task Locked_and_preview_chapters_are_not_listed()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Refresh(SuwayomiHarness.Manga(7, SeriesUrl),
            SuwayomiHarness.Chapter(1, "/c/1", "第1話", -1, sourceOrder: 1),
            SuwayomiHarness.Chapter(2, "/c/2", "\U0001F512 第2話", -1, sourceOrder: 2),
            SuwayomiHarness.Chapter(3, "/c/3", "\U0001F4B4 第3話", -1, sourceOrder: 3),
            SuwayomiHarness.Chapter(4, "/c/4", "\U0001F512 (Preview) 1巻", -1, sourceOrder: 4));

        var chapter = Assert.Single(await h.Source().ListChaptersAsync(SeriesUrl));

        Assert.Equal("/c/1", chapter.SourceChapterId);
    }

    [Fact]
    public async Task A_chapter_the_extension_did_not_number_takes_its_number_from_the_name()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Refresh(SuwayomiHarness.Manga(7, SeriesUrl),
            SuwayomiHarness.Chapter(1, "/c/1", "第1話: 第1話", -1, sourceOrder: 1),
            SuwayomiHarness.Chapter(2, "/c/2", "ｅｐｉｓｏｄｅ２\u3000ホットケーキ（１）", -1, sourceOrder: 2),
            SuwayomiHarness.Chapter(3, "/c/3", "ｅｐｉｓｏｄｅ２\u3000ホットケーキ（２）", -1, sourceOrder: 3),
            SuwayomiHarness.Chapter(4, "/c/4", "Ch. 5", 7, sourceOrder: 4));

        var chapters = await h.Source().ListChaptersAsync(SeriesUrl);

        Assert.Equal([1m, 2.1m, 2.2m, 7m], chapters.Select(c => c.Number).ToArray());
    }

    [Fact]
    public async Task A_mixed_language_source_files_chapters_under_the_language_the_admin_chose()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Refresh(SuwayomiHarness.Manga(7, SeriesUrl), SuwayomiHarness.Chapter(1, "/c/1", "Ch.1", 1));

        var source = h.Source(lang: "all", chapterLanguage: "es");
        var chapter = Assert.Single(await source.ListChaptersAsync(SeriesUrl));

        Assert.Equal("es", chapter.Language);
        Assert.Equal(["es"], source.SupportedLanguages);
    }

    [Fact]
    public async Task A_mixed_language_source_without_a_language_lists_nothing_rather_than_guessing()
    {
        var h = new SuwayomiHarness();
        var source = h.Source(lang: "all");

        Assert.True(source.NeedsSetup);
        await Assert.ThrowsAsync<SourceErrorException>(() => source.ListChaptersAsync(SeriesUrl));
        Assert.Empty(h.Server.Calls);
    }

    [Fact]
    public async Task A_v21_server_falls_back_to_fetchChapters_and_remembers_it()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Server.Ops["Refresh"] = (_, _) => FakeSuwayomi.Errors(
            "Validation error (FieldUndefined@[fetchMangaAndChapters]) : Field 'fetchMangaAndChapters' in type 'Mutation' is undefined");
        h.Server.Ops["MangaById"] = (_, _) => FakeSuwayomi.Data(new { manga = SuwayomiHarness.Manga(7, SeriesUrl) });
        h.Server.Ops["RefreshChapters"] = (vars, _) =>
        {
            Assert.Equal(7, vars.GetProperty("id").GetInt32());
            return FakeSuwayomi.Data(new { fetchChapters = new { chapters = new[] { SuwayomiHarness.Chapter(1, "/c/1", "Ch.1", 1) } } });
        };

        var source = h.Source();
        Assert.Single(await source.ListChaptersAsync(SeriesUrl));
        Assert.Single(await source.ListChaptersAsync(SeriesUrl));

        Assert.Equal(1, h.Server.Count("Refresh"));
        Assert.Equal(2, h.Server.Count("RefreshChapters"));
    }

    [Fact]
    public async Task On_a_v21_server_a_cached_id_naming_another_manga_is_still_caught()
    {
        var h = new SuwayomiHarness();
        h.Cache.SetManga(new Uri(SuwayomiHarness.Base), "100", SeriesUrl, 12);
        h.MangaByUrl(30);
        h.Server.Ops["Refresh"] = (_, _) => FakeSuwayomi.Errors(
            "Validation error (FieldUndefined@[fetchMangaAndChapters]) : Field 'fetchMangaAndChapters' in type 'Mutation' is undefined");
        h.Server.Ops["MangaById"] = (vars, _) => FakeSuwayomi.Data(new
        {
            manga = vars.GetProperty("id").GetInt32() == 12
                ? SuwayomiHarness.Manga(12, "/manga/someone-else", "Someone Else")
                : SuwayomiHarness.Manga(30, SeriesUrl)
        });
        h.Server.Ops["RefreshChapters"] = (vars, _) => FakeSuwayomi.Data(new
        {
            fetchChapters = new
            {
                chapters = vars.GetProperty("id").GetInt32() == 30
                    ? new[] { SuwayomiHarness.Chapter(1, "/c/ippo-1", "Ch.1", 1) }
                    : [SuwayomiHarness.Chapter(9, "/c/other-1", "Ch.1", 1)]
            }
        });

        var chapter = Assert.Single(await h.Source().ListChaptersAsync(SeriesUrl));

        Assert.Equal("/c/ippo-1", chapter.SourceChapterId);
        Assert.Equal(30, h.Cache.Manga(new Uri(SuwayomiHarness.Base), "100", SeriesUrl));
    }

    [Fact]
    public async Task A_chapter_the_site_took_down_is_a_404_so_the_download_moves_on()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Server.Ops["ChapterByUrl"] = (_, _) => FakeSuwayomi.Data(new { chapters = new { nodes = new[] { new { id = 42 } } } });
        h.Server.Ops["Pages"] = (_, _) => FakeSuwayomi.Errors("Exception while fetching data (/fetchChapterPages) : HTTP error 404");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => h.Source().GetPagesAsync(new SourceChapter("suwayomi-100", SeriesUrl, "/c/1", "Ch.1", 1, null, null, "en", null)));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task A_manga_this_suwayomi_has_never_seen_says_how_to_fix_it()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(null);

        var ex = await Assert.ThrowsAsync<SourceErrorException>(() => h.Source().ListChaptersAsync(SeriesUrl));

        Assert.Contains("Search for the title once in Suwayomi", ex.Message);
        Assert.Equal(0, h.Server.Count("Refresh"));
    }

    [Fact]
    public async Task A_cached_id_that_now_names_another_manga_is_resolved_again()
    {
        var h = new SuwayomiHarness();
        h.Cache.SetManga(new Uri(SuwayomiHarness.Base), "100", SeriesUrl, 7);
        h.MangaByUrl(9);
        h.Server.Ops["Refresh"] = (vars, _) => vars.GetProperty("id").GetInt32() == 7
            ? FakeSuwayomi.Data(new { fetchMangaAndChapters = new { manga = SuwayomiHarness.Manga(7, "/manga/someone-else"), chapters = Array.Empty<object>() } })
            : FakeSuwayomi.Data(new { fetchMangaAndChapters = new { manga = SuwayomiHarness.Manga(9, SeriesUrl), chapters = new[] { SuwayomiHarness.Chapter(1, "/c/1", "Ch.1", 1) } } });

        Assert.Single(await h.Source().ListChaptersAsync(SeriesUrl));

        Assert.Equal(9, h.Cache.Manga(new Uri(SuwayomiHarness.Base), "100", SeriesUrl));
    }

    [Fact]
    public async Task A_cached_manga_id_suwayomi_no_longer_has_is_forgotten_and_resolved_again()
    {
        var h = new SuwayomiHarness();
        var server = new Uri(SuwayomiHarness.Base);
        h.Cache.SetManga(server, "100", SeriesUrl, 7);
        h.MangaByUrl(9);
        h.Server.Ops["Refresh"] = (vars, _) => vars.GetProperty("id").GetInt32() == 7
            ? FakeSuwayomi.Errors("Exception while fetching data (/fetchMangaAndChapters) : No value present")
            : FakeSuwayomi.Data(new { fetchMangaAndChapters = new { manga = SuwayomiHarness.Manga(9, SeriesUrl), chapters = new[] { SuwayomiHarness.Chapter(1, "/c/1", "Ch.1", 1) } } });

        Assert.Single(await h.Source().ListChaptersAsync(SeriesUrl));

        Assert.Equal(9, h.Cache.Manga(server, "100", SeriesUrl));
    }

    [Fact]
    public async Task A_cached_chapter_id_suwayomi_no_longer_has_is_forgotten_and_resolved_again()
    {
        var h = new SuwayomiHarness();
        var server = new Uri(SuwayomiHarness.Base);
        h.Cache.SetManga(server, "100", SeriesUrl, 7);
        h.Cache.SetChapter(server, 7, "/c/2", 41);
        h.Server.Ops["ChapterByUrl"] = (_, _) => FakeSuwayomi.Data(new { chapters = new { nodes = new[] { new { id = 43 } } } });
        h.Server.Ops["Pages"] = (vars, _) => vars.GetProperty("chapterId").GetInt32() == 41
            ? FakeSuwayomi.Errors("Exception while fetching data (/fetchChapterPages) : No value present")
            : FakeSuwayomi.Data(new { fetchChapterPages = new { pages = new[] { "/api/v1/manga/7/chapter/2/page/0" }, chapter = new { url = "/c/2" } } });

        Assert.Single((await h.Source().GetPagesAsync(Chapter("/c/2"))).Pages);

        Assert.Equal(43, h.Cache.Chapter(server, 7, "/c/2"));
    }

    [Fact]
    public async Task A_site_error_on_a_cached_manga_id_is_not_asked_twice()
    {
        var h = new SuwayomiHarness();
        var server = new Uri(SuwayomiHarness.Base);
        h.Cache.SetManga(server, "100", SeriesUrl, 7);
        h.MangaByUrl(7);
        h.Server.Ops["Refresh"] = (_, _) => FakeSuwayomi.Errors("Exception while fetching data (/fetchMangaAndChapters) : The site is down");

        await Assert.ThrowsAsync<SuwayomiGraphQlException>(() => h.Source().ListChaptersAsync(SeriesUrl));

        Assert.Equal(1, h.Server.Count("Refresh"));
    }

    [Fact]
    public async Task A_site_error_on_a_cached_chapter_id_is_not_asked_twice()
    {
        var h = new SuwayomiHarness();
        var server = new Uri(SuwayomiHarness.Base);
        h.Cache.SetManga(server, "100", SeriesUrl, 7);
        h.Cache.SetChapter(server, 7, "/c/2", 41);
        h.Server.Ops["ChapterByUrl"] = (_, _) => FakeSuwayomi.Data(new { chapters = new { nodes = new[] { new { id = 41 } } } });
        h.Server.Ops["Pages"] = (_, _) => FakeSuwayomi.Errors("Exception while fetching data (/fetchChapterPages) : The site is down");

        await Assert.ThrowsAsync<SuwayomiGraphQlException>(() => h.Source().GetPagesAsync(Chapter("/c/2")));

        Assert.Equal(1, h.Server.Count("Pages"));
    }

    [Fact]
    public async Task An_uninstalled_extension_fails_before_anything_is_asked_of_it()
    {
        var h = new SuwayomiHarness();
        h.Installed("200");

        var ex = await Assert.ThrowsAsync<SourceErrorException>(() => h.Source().SearchAsync("Ippo"));

        Assert.Contains("no longer installed", ex.Message);
        Assert.Equal(0, h.Server.Count("Search"));
    }

    [Fact]
    public async Task The_installed_set_is_refetched_on_a_miss_at_most_once_a_minute()
    {
        var h = new SuwayomiHarness();
        h.Installed("200");
        var source = h.Source();

        await Assert.ThrowsAsync<SourceErrorException>(() => source.SearchAsync("Ippo"));
        await Assert.ThrowsAsync<SourceErrorException>(() => source.SearchAsync("Ippo"));
        Assert.Equal(1, h.Server.Count("InstalledSources"));

        h.Installed("100");
        h.Server.Ops["Search"] = (_, _) => FakeSuwayomi.Data(new { fetchSourceManga = new { mangas = Array.Empty<object>() } });
        h.Clock.Now += TimeSpan.FromMinutes(1);
        Assert.Empty(await source.SearchAsync("Ippo"));
        Assert.Equal(2, h.Server.Count("InstalledSources"));
    }

    [Fact]
    public async Task Pages_come_back_absolute_under_the_subpath_and_inside_the_origin()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Server.Ops["ChapterByUrl"] = (_, _) => FakeSuwayomi.Data(new { chapters = new { nodes = new[] { new { id = 42 } } } });
        h.Server.Ops["Pages"] = (vars, _) =>
        {
            Assert.Equal(42, vars.GetProperty("chapterId").GetInt32());
            return FakeSuwayomi.Data(new
            {
                fetchChapterPages = new
                {
                    pages = new[] { "/api/v1/manga/7/chapter/1/page/0", "/api/v1/manga/7/chapter/1/page/1" },
                    chapter = new { url = "/c/1" }
                }
            });
        };

        var source = h.Source();
        var pages = await source.GetPagesAsync(Chapter("/c/1"));

        Assert.Equal(
            [$"{SuwayomiHarness.Base}/api/v1/manga/7/chapter/1/page/0", $"{SuwayomiHarness.Base}/api/v1/manga/7/chapter/1/page/1"],
            pages.Pages.Select(p => p.Url));
        Assert.All(pages.Pages, p => Assert.True(source.IsPageUrlAllowed(new Uri(p.Url))));
        Assert.Null(pages.Pages[0].Headers);
        Assert.Equal(SuwayomiClient.PagesHttpClientName, source.PageHttpClientName);
        Assert.False(source.IsPageUrlAllowed(new Uri("http://192.168.1.20:8080/api/v1/manga/7/chapter/1/page/0")));
        Assert.False(source.IsPageUrlAllowed(new Uri("https://cdn.site.test/page.jpg")));
    }

    [Fact]
    public async Task A_chapter_suwayomi_does_not_list_is_refreshed_once_then_reads_as_a_404()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Server.Ops["ChapterByUrl"] = (_, _) => FakeSuwayomi.Data(new { chapters = new { nodes = Array.Empty<object>() } });
        h.Refresh(SuwayomiHarness.Manga(7, SeriesUrl), SuwayomiHarness.Chapter(1, "/c/1", "Ch.1", 1));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => h.Source().GetPagesAsync(Chapter("/c/2")));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(1, h.Server.Count("Refresh"));
        Assert.Equal(0, h.Server.Count("Pages"));
    }

    [Fact]
    public async Task A_chapter_missing_from_suwayomis_older_list_is_found_after_one_refresh()
    {
        var h = new SuwayomiHarness();
        h.MangaByUrl(7);
        h.Server.Ops["ChapterByUrl"] = (_, _) => FakeSuwayomi.Data(new { chapters = new { nodes = Array.Empty<object>() } });
        h.Refresh(SuwayomiHarness.Manga(7, SeriesUrl), SuwayomiHarness.Chapter(43, "/c/2", "Ch.2", 2));
        h.Server.Ops["Pages"] = (vars, _) =>
        {
            Assert.Equal(43, vars.GetProperty("chapterId").GetInt32());
            return FakeSuwayomi.Data(new { fetchChapterPages = new { pages = new[] { "/api/v1/manga/7/chapter/2/page/0" }, chapter = new { url = "/c/2" } } });
        };

        Assert.Single((await h.Source().GetPagesAsync(Chapter("/c/2"))).Pages);
    }

    [Fact]
    public void Rating_follows_the_content_warning_and_v21s_nsfw_flag()
    {
        var h = new SuwayomiHarness();
        Assert.Equal(SourceRating.Adult, h.Source(contentWarning: "NSFW").Rating);
        Assert.Equal(SourceRating.Mature, h.Source(contentWarning: "MIXED").Rating);
        Assert.Equal(SourceRating.General, h.Source(contentWarning: "SAFE").Rating);

        var legacy = new SuwayomiSource(
            new SuwayomiStoredSource("100", "Fake", "Fake", "en", IsNsfw: true), new Uri(SuwayomiHarness.Base), h.Client, h.Cache);
        Assert.Equal(SourceRating.Adult, legacy.Rating);
        Assert.Equal("suwayomi-100", legacy.Name);
        Assert.Equal(SuwayomiHarness.Base, legacy.BaseUrl);
    }

    [Theory]
    [InlineData("Vol.3 Ch.15 - The Fight", "The Fight")]
    [InlineData("Chapter 10.5: Side Story", "Side Story")]
    [InlineData("Ch.7", null)]
    [InlineData("Extra: Behind the scenes", null)]
    public void Title_is_the_part_after_the_number(string name, string? expected) =>
        Assert.Equal(expected, SuwayomiSource.TitleOf(name));

    private static SourceChapter Chapter(string url) =>
        new("suwayomi-100", SeriesUrl, url, "Ch.1", 1, null, null, "en", null);
}

public class SuwayomiAuthTests
{
    private static void LoginReturns(SuwayomiHarness h, string access, string refresh = "r1") =>
        h.Server.Ops["Login"] = (vars, request) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("maki", vars.GetProperty("username").GetString());
            return FakeSuwayomi.Data(new { login = new { accessToken = access, refreshToken = refresh } });
        };

    private static void SearchAccepts(SuwayomiHarness h, string token) =>
        h.Server.Ops["Search"] = (_, request) => request.Headers.Authorization?.Parameter == token
            ? FakeSuwayomi.Data(new { fetchSourceManga = new { mangas = Array.Empty<object>() } })
            : FakeSuwayomi.Errors("Unauthorized");

    [Fact]
    public async Task Ui_login_signs_in_once_and_sends_the_bearer_token()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "pw");
        LoginReturns(h, "a1");
        SearchAccepts(h, "a1");

        await h.Client.SearchAsync("100", "x");
        await h.Client.SearchAsync("100", "y");

        Assert.Equal(1, h.Server.Count("Login"));
        Assert.All(h.Server.Calls.Where(c => c.Op == "Search"), c => Assert.Equal("Bearer a1", c.Auth));
    }

    [Theory]
    [InlineData("Unauthorized", true)]
    [InlineData("unauthorized.", true)]
    [InlineData("Exception while fetching data (/sources) : Unauthorized", true)]
    [InlineData("Unauthorized access to this chapter", false)]
    [InlineData("Exception while fetching data (/fetchChapterPages) : Request failed: Unauthorized area, log in on the site", false)]
    [InlineData("HTTP error 401 Unauthorized", false)]
    public void Only_suwayomis_own_rejection_counts_as_unauthorized(string message, bool expected) =>
        Assert.Equal(expected, SuwayomiGraphQl.IsUnauthorized(message));

    [Fact]
    public async Task A_site_error_that_mentions_unauthorized_is_not_a_credential_failure()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "pw");
        LoginReturns(h, "a1");
        h.Server.Ops["Search"] = (_, _) => FakeSuwayomi.Errors("Exception while fetching data (/fetchSourceManga) : The site said Unauthorized region");

        var ex = await Assert.ThrowsAsync<SuwayomiGraphQlException>(() => h.Client.SearchAsync("100", "x"));

        Assert.Contains("Unauthorized region", ex.Message);
        Assert.Equal(1, h.Server.Count("Login"));
        Assert.Equal(1, h.Server.Count("Search"));
    }

    private sealed class StallingContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await Task.Delay(Timeout.Infinite);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task A_body_that_stalls_times_out_like_the_client_would()
    {
        using var content = new StallingContent();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            SuwayomiGraphQl.BufferBodyAsync(content, CancellationToken.None, TimeSpan.FromMilliseconds(100)));

        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task A_caller_cancelling_a_body_read_stays_a_plain_cancellation()
    {
        using var content = new StallingContent();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SuwayomiGraphQl.BufferBodyAsync(content, cts.Token, TimeSpan.FromMinutes(1)));

        Assert.Null(ex.InnerException as TimeoutException);
    }

    [Fact]
    public async Task A_200_unauthorized_refreshes_once_for_every_caller_rejected_with_the_same_token()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "pw");
        LoginReturns(h, "old");
        h.Server.Ops["RefreshToken"] = (vars, request) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("r1", vars.GetProperty("refreshToken").GetString());
            return FakeSuwayomi.Data(new { refreshToken = new { accessToken = "new" } });
        };
        SearchAccepts(h, "new");

        // Every caller's first attempt is held until all six are in flight with the old token, so
        // they are rejected together and race to renew it.
        const int callers = 6;
        var rejectedTogether = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        h.Server.Hold = (op, request) =>
        {
            if (op != "Search" || request.Headers.Authorization?.Parameter != "old")
            {
                return Task.CompletedTask;
            }

            if (Interlocked.Increment(ref held) == callers)
            {
                rejectedTogether.SetResult();
            }

            return rejectedTogether.Task;
        };

        await Task.WhenAll(Enumerable.Range(0, callers).Select(i => Task.Run(() => h.Client.SearchAsync("100", $"q{i}"))))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(callers, held);

        Assert.Equal(1, h.Server.Count("Login"));
        Assert.Equal(1, h.Server.Count("RefreshToken"));
    }

    [Fact]
    public async Task A_refused_refresh_token_signs_in_again()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "pw");
        var logins = 0;
        h.Server.Ops["Login"] = (_, _) =>
            FakeSuwayomi.Data(new { login = new { accessToken = ++logins == 1 ? "old" : "fresh", refreshToken = "r1" } });
        h.Server.Ops["RefreshToken"] = (_, _) => FakeSuwayomi.Errors("Invalid refresh token");
        SearchAccepts(h, "fresh");

        await h.Client.SearchAsync("100", "x");

        Assert.Equal(2, h.Server.Count("Login"));
    }

    [Fact]
    public async Task A_401_on_a_page_renews_and_retries_without_reading_the_body()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "pw");
        LoginReturns(h, "old");
        h.Server.Ops["RefreshToken"] = (_, _) => FakeSuwayomi.Data(new { refreshToken = new { accessToken = "new" } });
        h.Server.OnGet = request => request.Headers.Authorization?.Parameter == "new"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]) }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized);

        using var response = await h.CreateClient(SuwayomiClient.PagesHttpClientName)
            .GetAsync($"{SuwayomiHarness.Base}/api/v1/manga/7/chapter/1/page/0", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, h.Server.Calls.Count(c => c.Op.StartsWith("GET ")));
    }

    [Fact]
    public async Task Basic_auth_is_sent_as_is_and_a_rejection_is_not_retried()
    {
        var h = new SuwayomiHarness(authMode: "basic", username: "maki", password: "pw");
        h.Server.Ops["Search"] = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.SearchAsync("100", "x"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        var call = Assert.Single(h.Server.Calls, c => c.Op == "Search");
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("maki:pw")), call.Auth);
    }

    [Fact]
    public async Task Changing_the_password_discards_the_old_tokens()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "pw");
        LoginReturns(h, "a1");
        SearchAccepts(h, "a1");
        await h.Client.SearchAsync("100", "x");

        await h.Settings.SetAsync(SettingKeys.SuwayomiPassword, "pw2");
        await h.Client.SearchAsync("100", "y");

        Assert.Equal(2, h.Server.Count("Login"));
    }

    [Fact]
    public async Task A_wrong_password_reads_as_refused_and_is_not_retried_on_every_request()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "wrong");
        h.Server.Ops["Login"] = (_, _) => FakeSuwayomi.Errors(FakeSuwayomi.LoginRefused);
        SearchAccepts(h, "a1");

        var first = await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.SearchAsync("100", "x"));
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.SearchAsync("100", "y"));
        Assert.Equal(1, h.Server.Count("Login"));

        h.Clock.Now += TimeSpan.FromSeconds(31);
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.SearchAsync("100", "z"));
        Assert.Equal(2, h.Server.Count("Login"));
    }

    [Fact]
    public async Task Fixing_the_password_is_tried_at_once_rather_than_after_the_hold()
    {
        var h = new SuwayomiHarness(authMode: "uilogin", username: "maki", password: "wrong");
        h.Server.Ops["Login"] = (vars, _) => vars.GetProperty("password").GetString() == "right"
            ? FakeSuwayomi.Data(new { login = new { accessToken = "a1", refreshToken = "r1" } })
            : FakeSuwayomi.Errors(FakeSuwayomi.LoginRefused);
        SearchAccepts(h, "a1");
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Client.SearchAsync("100", "x"));

        await h.Settings.SetAsync(SettingKeys.SuwayomiPassword, "right");
        await h.Client.SearchAsync("100", "y");

        Assert.Equal(2, h.Server.Count("Login"));
    }
}

public class SuwayomiTransportTests
{
    [Fact]
    public async Task The_clients_refuse_anything_outside_the_configured_origin()
    {
        var h = new SuwayomiHarness();
        var client = h.CreateClient(SuwayomiClient.PagesHttpClientName);

        await Assert.ThrowsAsync<BlockedDestinationException>(() => client.GetAsync("http://192.168.1.20:8080/api/v1/manga/1/thumbnail"));
        await Assert.ThrowsAsync<BlockedDestinationException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data"));
        Assert.Empty(h.Server.Calls);
    }

    [Fact]
    public async Task A_redirect_is_refused_rather_than_followed()
    {
        var h = new SuwayomiHarness();
        h.Server.OnGet = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://169.254.169.254/");
            return response;
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => h.CreateClient(SuwayomiClient.PagesHttpClientName).GetAsync($"{SuwayomiHarness.Base}/api/v1/manga/1/thumbnail"));

        Assert.Equal(HttpStatusCode.Found, ex.StatusCode);
    }

    [Fact]
    public async Task An_error_without_a_message_still_fails_and_a_site_429_is_a_rate_limit()
    {
        var h = new SuwayomiHarness();
        h.Server.Ops["Search"] = (_, _) => FakeSuwayomi.Errors([null]);
        var ex = await Assert.ThrowsAsync<SuwayomiGraphQlException>(() => h.Client.SearchAsync("100", "x"));
        Assert.Contains("without a message", ex.Message);

        h.Server.Ops["Search"] = (_, _) => FakeSuwayomi.Errors("HTTP error 429");
        await Assert.ThrowsAsync<RateLimitException>(() => h.Client.SearchAsync("100", "x"));
    }

    [Fact]
    public async Task The_health_probe_skips_the_queue_other_calls_wait_in()
    {
        var h = new SuwayomiHarness();
        var skipped = new List<bool>();
        h.Server.Ops["Sources"] = (_, request) =>
        {
            skipped.Add(request.Options.TryGetValue(RateLimitingHandler.Skip, out var skip) && skip);
            return FakeSuwayomi.Data(new { sources = new { nodes = Array.Empty<object>() } });
        };

        await h.Client.ListSourcesAsync(probe: true);
        await h.Client.ListSourcesAsync();

        Assert.Equal([true, false], skipped);
    }

    [Fact]
    public void The_id_cache_empties_a_map_that_outgrows_its_cap()
    {
        var cache = new SuwayomiMangaIdCache();
        var server = new Uri(SuwayomiHarness.Base);
        for (var i = 0; i < SuwayomiMangaIdCache.MaxEntries; i++)
        {
            cache.SetChapter(server, 7, $"/c/{i}", i);
        }

        Assert.Equal(5, cache.Chapter(server, 7, "/c/5"));
        cache.SetChapter(server, 7, "/c/next", 1);

        Assert.Null(cache.Chapter(server, 7, "/c/5"));
        Assert.Equal(1, cache.Chapter(server, 7, "/c/next"));
    }

    [Fact]
    public async Task A_v21_source_list_is_read_without_content_warnings()
    {
        var h = new SuwayomiHarness();
        h.Server.Ops["Sources"] = (_, request) =>
        {
            var body = request.Content!.ReadAsStringAsync().Result;
            return body.Contains("contentWarning")
                ? FakeSuwayomi.Errors("Validation error (FieldUndefined@[sources/nodes/contentWarning]) : Field 'contentWarning' in type 'SourceType' is undefined")
                : FakeSuwayomi.Data(new { sources = new { nodes = new[] { new { id = "100", name = "Fake", lang = "en", displayName = "Fake (EN)", isNsfw = true } } } });
        };

        var source = Assert.Single(await h.Client.ListSourcesAsync());

        Assert.Equal("100", source.Id);
        Assert.True(source.IsNsfw);
        Assert.Null(source.ContentWarning);
    }

    [Theory]
    [InlineData("http://suwayomi:4567/", "http://suwayomi:4567")]
    [InlineData("http://suwayomi:4567/sub/", "http://suwayomi:4567/sub")]
    [InlineData(" https://manga.example.test ", "https://manga.example.test")]
    public void Base_urls_lose_their_trailing_slash(string input, string expected)
    {
        Assert.True(SuwayomiSettings.TryParseBaseUri(input, out var uri));
        Assert.Equal(expected, uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("suwayomi:4567")]
    [InlineData("ftp://suwayomi")]
    [InlineData("http://user:pw@suwayomi:4567")]
    [InlineData("http://suwayomi:4567/?x=1")]
    public void Unusable_base_urls_turn_the_integration_off(string input) =>
        Assert.False(SuwayomiSettings.TryParseBaseUri(input, out _));

    [Fact]
    public void Returned_paths_resolve_under_the_subpath()
    {
        Assert.True(SuwayomiSettings.TryParseBaseUri("http://host:4567/sub", out var baseUri));
        var connection = new SuwayomiConnection(baseUri, SuwayomiAuthMode.None, null, null);

        Assert.Equal("http://host:4567/sub/api/graphql", connection.GraphQlUri.ToString());
        Assert.Equal("http://host:4567/sub/api/v1/manga/7/chapter/1/page/0?format=webp",
            connection.Resolve("/api/v1/manga/7/chapter/1/page/0?format=webp").ToString());
        Assert.Throws<InvalidOperationException>(() => connection.Resolve("http://elsewhere.test/x"));
    }
}

public class SuwayomiSourceProviderTests
{
    [Fact]
    public async Task Every_stored_source_becomes_a_source_and_bad_ids_are_skipped()
    {
        var h = new SuwayomiHarness();
        await h.Settings.SetAsync(SettingKeys.SuwayomiSources, SuwayomiSettings.Serialize(
        [
            new SuwayomiStoredSource("100", "MangaDex", "MangaDex (EN)", "en"),
            new SuwayomiStoredSource("200", "Comick", "Comick (ALL)", "all"),
            new SuwayomiStoredSource("300", "Comick", "Comick (ALL)", "all", ChapterLanguage: "fr"),
            new SuwayomiStoredSource("not-an-id", "Broken", "Broken", "en"),
            new SuwayomiStoredSource("100", "MangaDex", "MangaDex (EN)", "en")
        ]));
        var provider = new SuwayomiSourceProvider(h.SuwayomiSettings, h.Client, h.Cache, NullLogger<SuwayomiSourceProvider>.Instance);

        Assert.Empty(provider.Current);
        await provider.ReloadAsync();

        Assert.Equal(["suwayomi-100", "suwayomi-200", "suwayomi-300"], provider.Current.Select(s => s.Name));
        var unset = Assert.IsType<SuwayomiSource>(provider.Current[1]);
        Assert.True(unset.NeedsSetup);
        Assert.Empty(unset.SupportedLanguages);
        Assert.Equal(["fr"], provider.Current[2].SupportedLanguages);

        var registry = new SourceRegistry([], [provider]);
        await registry.ReloadAsync();
        Assert.NotNull(registry.Find("suwayomi-300"));
    }

    [Fact]
    public async Task Nothing_is_supplied_without_a_url_or_with_a_malformed_list()
    {
        var h = new SuwayomiHarness();
        var provider = new SuwayomiSourceProvider(h.SuwayomiSettings, h.Client, h.Cache, NullLogger<SuwayomiSourceProvider>.Instance);

        await h.Settings.SetAsync(SettingKeys.SuwayomiSources, "{not json");
        await provider.ReloadAsync();
        Assert.Empty(provider.Current);

        await h.Settings.SetAsync(SettingKeys.SuwayomiSources,
            SuwayomiSettings.Serialize([new SuwayomiStoredSource("100", "MangaDex", "MangaDex (EN)", "en")]));
        await h.Settings.SetAsync(SettingKeys.SuwayomiUrl, null);
        await provider.ReloadAsync();
        Assert.Empty(provider.Current);
    }
}

public class SuwayomiVersionTests
{
    [Theory]
    [InlineData("v2.4.2366", "none", null)]
    [InlineData("v2.1.1867", "basic", null)]
    [InlineData("v2.0.1727", "none", "v2.1")]
    [InlineData("v2.1.1867", "uilogin", "v2.2")]
    [InlineData("v2.2.2100", "uilogin", null)]
    [InlineData("v3.0.0", "uilogin", null)]
    [InlineData("preview-abc123", "uilogin", null)]
    public void A_sign_in_mode_has_its_own_minimum(string version, string mode, string? expected) =>
        Assert.Equal(expected, SuwayomiVersion.Unmet(version, SuwayomiSettings.ParseAuthMode(mode)));
}
