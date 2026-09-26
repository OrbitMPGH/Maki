using Maki.Core.Sources;
using Maki.Sources.MangaLib;

namespace Maki.Sources.Tests;

public class MangaLibSourceTests
{
    private static MangaLibSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static MangaLibSource WithSearch() =>
        SourceFor(new() { ["api/manga?q="] = FakeHttpClientFactory.Fixture("mangalib-search.json") });

    private static MangaLibSource WithSeries() =>
        SourceFor(new() { ["api/manga/206--one-piece?"] = FakeHttpClientFactory.Fixture("mangalib-series.json") });

    private static MangaLibSource WithChapters() =>
        SourceFor(new()
        {
            ["api/manga/206--one-piece/chapters"] = FakeHttpClientFactory.Fixture("mangalib-chapters.json")
        });

    [Fact]
    public async Task Search_maps_hits_preferring_the_english_title()
    {
        var results = await WithSearch().SearchAsync("one piece");

        Assert.Equal(5, results.Count);
        Assert.Equal("206--one-piece", results[0].SourceSeriesId);
        Assert.Equal("One Piece", results[0].Title);
        Assert.Equal("One Piece A", results[1].Title);
        Assert.Equal("https://mangalib.me/ru/manga/206--one-piece", results[0].Url);
        Assert.Equal(
            "https://cover.cdnlibs.org/uploads/cover/one-piece/cover/89a48c0c-4c5d-4636-8143-5933ae1da6bb.jpg",
            results[0].CoverUrl);
    }

    [Fact]
    public async Task Search_falls_back_to_original_title()
    {
        var results = await WithSearch().SearchAsync("one piece");

        Assert.Equal("One Piece Special - Roronoa Zoro Falls Into the Sea", results[3].Title);
        // Both rus_name and eng_name are blank here; only "name" is left.
        Assert.Equal("One piece dj - Sani de Kakurenbo", results[4].Title);
    }

    [Fact]
    public async Task Search_returns_null_cover_when_the_row_carries_none()
    {
        var results = await WithSearch().SearchAsync("one piece");

        Assert.Null(results[4].CoverUrl);
    }

    [Fact]
    public async Task GetSeries_reads_title_status_cover_and_joins_summary_paragraphs()
    {
        var detail = await WithSeries().GetSeriesAsync("206--one-piece");

        Assert.Equal("Ван Пис", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal("https://mangalib.me/ru/manga/206--one-piece", detail.Url);
        Assert.Equal(
            "https://cover.cdnlibs.org/uploads/cover/one-piece/cover/89a48c0c-4c5d-4636-8143-5933ae1da6bb.jpg",
            detail.CoverUrl);
        Assert.Equal(
            "Gold Roger was king of the pirates. His last words sent the world into an age of pirates.\n" +
            "A rookie named Luffy sets sail to find the One Piece.",
            detail.Description);
    }

    [Fact]
    public async Task ListChapters_picks_the_first_open_branch_and_skips_fully_closed_chapters()
    {
        var chapters = await WithChapters().ListChaptersAsync("206--one-piece");

        // Chapter "3" (index 3) has one branch and it's closed: skipped entirely.
        // Chapters "4" and "5" (index 4, 5) have no number at all but are still listed, with
        // Number null, per 00-GENERAL 3.2 (an unparseable number is null, never dropped). Null
        // sorts first under the default comparer, so they lead rather than trail the list.
        Assert.Equal([null, null, 1m, 2.5m], chapters.Select(c => c.Number));

        var first = chapters.Single(c => c.Number == 1m);
        Assert.Equal("1|1|3978", first.SourceChapterId);
        Assert.Equal("ru", first.Language);
        Assert.Equal(1, first.Volume);
        Assert.Equal("Прототип", first.Title);
        Assert.Equal(new DateTime(2021, 6, 14, 0, 54, 44, DateTimeKind.Utc), first.ReleaseDate);

        // The 2.5 chapter's first branch is closed; the second (open) branch is the one chosen.
        var second = chapters.Single(c => c.Number == 2.5m);
        Assert.Equal("1|2.5|4002", second.SourceChapterId);
        Assert.Null(second.Title);
        Assert.Equal(new DateTime(2021, 6, 21, 0, 0, 0, DateTimeKind.Utc), second.ReleaseDate);
    }

    [Fact]
    public async Task ListChapters_keeps_a_titled_extra_with_no_number_and_a_null_number()
    {
        var chapters = await WithChapters().ListChaptersAsync("206--one-piece");

        var announcement = chapters.Single(c => c.SourceChapterId == "1||4004");
        Assert.Null(announcement.Number);
        Assert.Equal("Announcement only", announcement.Title);
    }

    [Fact]
    public async Task ListChapters_builds_a_fallback_title_for_an_untitled_extra_with_no_number()
    {
        var chapters = await WithChapters().ListChaptersAsync("206--one-piece");

        // Neither "name" nor "number" is set on this row, so a fallback is needed: two blank
        // titles on null-Number chapters would otherwise alias into one row on sync (identity is
        // IsOneShot + Language + Title, Volume ignored).
        var extra = chapters.Single(c => c.SourceChapterId == "2||4005");
        Assert.Null(extra.Number);
        Assert.Equal("Vol. 2 extra", extra.Title);
    }

    [Fact]
    public async Task GetPages_orders_by_slug_and_builds_urls_from_the_download_image_server()
    {
        var source = SourceFor(new()
        {
            ["api/manga/206--one-piece/chapter?"] = FakeHttpClientFactory.Fixture("mangalib-pages.json"),
            ["api/constants"] = FakeHttpClientFactory.Fixture("mangalib-constants.json")
        });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "mangalib", "206--one-piece", "108|1194|", "1194", 1194m, 108, null, "ru", null));

        Assert.Equal(13, pages.Pages.Count);
        Assert.Equal(
            "https://img3.cdnlibs.org//manga/one-piece/chapters/4552795/4d451765-39fd-415c-9eef-1dd693bd0b5e.jpg",
            pages.Pages[0].Url);
        Assert.All(pages.Pages, p => Assert.Equal("https://mangalib.me/", p.Headers!["Referer"]));
        // "download" is img3.cdnlibs.org for site_id 1, not "main" (img2.imglib.info, which serves
        // AVIF bytes under a .jpg name).
        Assert.DoesNotContain(pages.Pages, p => p.Url.Contains("img2.imglib.info"));
    }

    [Fact]
    public async Task GetPages_throws_ChapterLocked_when_the_chapter_serves_no_pages()
    {
        var source = SourceFor(new()
        {
            ["api/manga/206--one-piece/chapter?"] = "{\"data\":{\"pages\":[]}}",
            ["api/constants"] = FakeHttpClientFactory.Fixture("mangalib-constants.json")
        });

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(new SourceChapter(
            "mangalib", "206--one-piece", "1|9999|", "9999", 9999m, 1, null, "ru", null)));
    }
}
