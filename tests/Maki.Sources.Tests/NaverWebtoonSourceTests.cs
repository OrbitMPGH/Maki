using System.Net;
using Maki.Core.Sources;
using Maki.Sources.NaverWebtoon;

namespace Maki.Sources.Tests;

public class NaverWebtoonSourceTests
{
    [Fact]
    public async Task SearchAsync_ParsesIdAndTitle()
    {
        var source = new NaverWebtoonSource(new FakeHttpClientFactory(new()
        {
            ["api/search/webtoon"] = FakeHttpClientFactory.Fixture("naverwebtoon-search.json"),
        }));

        var results = await source.SearchAsync("화산귀환");

        var hit = Assert.Single(results);
        Assert.Equal("769209", hit.SourceSeriesId);
        Assert.Equal("화산귀환", hit.Title);
        Assert.Equal("https://comic.naver.com/webtoon/list?titleId=769209", hit.Url);
        Assert.Contains("thumbnail_IMAG21", hit.CoverUrl);
    }

    [Fact]
    public async Task SearchAsync_DropsAdultHits()
    {
        var source = new NaverWebtoonSource(new FakeHttpClientFactory(new()
        {
            ["api/search/webtoon"] = FakeHttpClientFactory.Fixture("naverwebtoon-search-adult.json"),
        }));

        var results = await source.SearchAsync("복수");

        Assert.Equal(9, results.Count);
        Assert.DoesNotContain(results, r => r.SourceSeriesId == "852164");
    }

    [Fact]
    public async Task GetSeriesAsync_MapsOngoingStatus()
    {
        var source = new NaverWebtoonSource(new FakeHttpClientFactory(new()
        {
            ["api/article/list/info"] = FakeHttpClientFactory.Fixture("naverwebtoon-info.json"),
        }));

        var detail = await source.GetSeriesAsync("769209");

        Assert.Equal("화산귀환", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal("https://comic.naver.com/webtoon/list?titleId=769209", detail.Url);
        Assert.Contains("thumbnail_IMAG21", detail.CoverUrl);
    }

    [Fact]
    public async Task ListChaptersAsync_WalksPagesAndFiltersLockedChapters()
    {
        // list-1's nextPage is 2 in the recorded fixture; list-last is reused under that key so
        // the walk stops after two requests without needing every intermediate page recorded.
        var source = new NaverWebtoonSource(new FakeHttpClientFactory(new()
        {
            ["api/article/list?titleId=769209&page=1"] = FakeHttpClientFactory.Fixture("naverwebtoon-list-1.json"),
            ["api/article/list?titleId=769209&page=2"] = FakeHttpClientFactory.Fixture("naverwebtoon-list-last.json"),
        }));

        var chapters = await source.ListChaptersAsync("769209");

        // Page 1: 20 free episodes (charge false). chargeFolderArticleList previews are ignored
        // entirely. Page 2 (the reused last-page fixture): 2 more free episodes (no 1, 2).
        Assert.Equal(22, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("ko", c.Language));
        Assert.Contains(chapters, c => c.Number == 1);
        Assert.Contains(chapters, c => c.Number == 182);
        Assert.DoesNotContain(chapters, c => c.Number is >= 183 and <= 187);

        var ch182 = Assert.Single(chapters, c => c.Number == 182);
        Assert.Equal("182", ch182.SourceChapterId);
        Assert.Equal("176화", ch182.Title);
        Assert.Equal(new DateTime(2026, 9, 21, 15, 0, 0, DateTimeKind.Utc), ch182.ReleaseDate);
        Assert.Equal("https://comic.naver.com/webtoon/detail?titleId=769209&no=182", ch182.Url);
    }

    [Fact]
    public async Task ListChaptersAsync_AdultTitle_ThrowsInvalidOperation()
    {
        // A 401 has no fixture body of its own; use a tiny handler that always answers 401.
        var source = new NaverWebtoonSource(new UnauthorizedHttpClientFactory());

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("852164"));
    }

    [Fact]
    public async Task GetPagesAsync_ReturnsAllPagesWithReferer()
    {
        var source = new NaverWebtoonSource(new FakeHttpClientFactory(new()
        {
            ["webtoon/detail?titleId=769209&no=182"] = FakeHttpClientFactory.Fixture("naverwebtoon-detail.html"),
        }));

        var chapter = new SourceChapter(
            "naverwebtoon", "769209", "182", "182", 182, null, "176화", "ko", null,
            "https://comic.naver.com/webtoon/detail?titleId=769209&no=182");

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(104, pages.Pages.Count);
        Assert.All(pages.Pages, p => Assert.Equal("https://comic.naver.com/", p.Headers?["Referer"]));
        Assert.StartsWith("https://image-comic.pstatic.net/", pages.Pages[0].Url);
    }

    [Fact]
    public async Task GetPagesAsync_LockedChapter_ThrowsChapterLocked()
    {
        var handler = new FixedFinalUriHandler(new Uri("https://fixture.test/webtoon/list?titleId=769209"));
        var source = new NaverWebtoonSource(new SingleHandlerHttpClientFactory(handler));
        var chapter = new SourceChapter(
            "naverwebtoon", "769209", "187", "187", 187, null, "181화", "ko", null, null);

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(chapter));
    }

    [Fact]
    public async Task GetPagesAsync_AdultChapter_ThrowsInvalidOperation()
    {
        var handler = new FixedFinalUriHandler(new Uri("https://nid.naver.com/nidlogin.login"));
        var source = new NaverWebtoonSource(new SingleHandlerHttpClientFactory(handler));
        var chapter = new SourceChapter(
            "naverwebtoon", "852164", "1", "1", 1, null, null, "ko", null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPagesAsync(chapter));
    }

    /// <summary>Always answers 200 with a caller-chosen RequestMessage.RequestUri, simulating a
    /// followed redirect without needing a real HTTP redirect round trip.</summary>
    private class FixedFinalUriHandler(Uri finalUri) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri),
                Content = new StringContent(string.Empty)
            });
    }

    private class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("https://fixture.test/") };
    }

    /// <summary>Always answers 401, simulating Naver's adult-title chapter-list response.</summary>
    private class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private class UnauthorizedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new UnauthorizedHandler()) { BaseAddress = new Uri("https://fixture.test/") };
    }
}
