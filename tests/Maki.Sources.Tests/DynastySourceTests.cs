using Maki.Core.Sources;
using Maki.Sources.Dynasty;

namespace Maki.Sources.Tests;

public class DynastySourceTests
{
    private static DynastySource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static DynastySource WithSearch() =>
        SourceFor(new() { ["search?q=citrus"] = FakeHttpClientFactory.Fixture("dynasty-search.html") });

    private static DynastySource WithSeries() =>
        SourceFor(new() { ["series/citrus.json"] = FakeHttpClientFactory.Fixture("dynasty-series.json") });

    private static DynastySource WithLongSeries() =>
        SourceFor(new() { ["series/yuru_yuri.json"] = FakeHttpClientFactory.Fixture("dynasty-series-long.json") });

    [Fact]
    public async Task Search_reads_series_hits_from_the_html_page()
    {
        // /search.json 500s on the live site; this is the only search endpoint there is.
        var results = await WithSearch().SearchAsync("citrus");

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.SourceSeriesId == "citrus" && r.Title == "Citrus");
        Assert.Contains(results, r => r.SourceSeriesId == "citrus_1" && r.Title == "Citrus +");
        Assert.All(results, r => Assert.Null(r.CoverUrl));
        Assert.All(results, r => Assert.StartsWith("https://dynasty-scans.com/series/", r.Url));
    }

    [Fact]
    public async Task GetSeries_reads_title_status_cover_and_plain_text_description()
    {
        var detail = await WithSeries().GetSeriesAsync("citrus");

        Assert.Equal("Citrus", detail.Title);
        Assert.Equal("Completed", detail.Status);
        Assert.Equal(
            "https://dynasty-scans.com/system/tag_contents_covers/000/002/083/medium/81dPnJl7iUL.jpg?1437939274",
            detail.CoverUrl);
        Assert.Equal("Continued in Citrus +", detail.Description);
        Assert.Equal("https://dynasty-scans.com/series/citrus", detail.Url);
    }

    [Fact]
    public async Task GetSeries_throws_for_a_non_series_entry()
    {
        // Doujins/anthologies/issues are collections of unrelated one-shots; they don't map
        // to one MangaBaka entry, so they must not silently read as an empty series.
        var source = SourceFor(new()
        {
            ["series/some-doujin.json"] = """{"name":"Some Doujin","type":"Doujin","permalink":"some-doujin","tags":[],"taggings":[]}"""
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetSeriesAsync("some-doujin"));
    }

    [Fact]
    public async Task ListChapters_parses_forty_one_numbered_chapters_across_ten_volumes()
    {
        var chapters = await WithSeries().ListChaptersAsync("citrus");

        var numbered = chapters.Where(c => c.Number is not null).ToList();
        Assert.Equal(41, numbered.Count);
        Assert.Equal(1m, numbered.First().Number);
        Assert.Equal(41m, numbered.Last().Number);
        Assert.Equal(
            new int?[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            numbered.Select(c => c.Volume).Distinct().OrderBy(v => v));
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
    }

    [Fact]
    public async Task ListChapters_keeps_several_specials_in_one_volume()
    {
        var chapters = await WithSeries().ListChaptersAsync("citrus");

        var specials = chapters.Where(c => c.Number is null && c.Volume == 5).ToList();
        Assert.Equal(
            new[] { "love triangle !?", "Vol. 5 Special 2", "Vol. 5 Special 3", "Vol. 5 Extra" },
            specials.Select(c => c.Title));
    }

    [Fact]
    public async Task ListChapters_gives_a_null_number_and_volume_to_extras_outside_any_volume()
    {
        var chapters = await WithSeries().ListChaptersAsync("citrus");

        var extras = chapters.Where(c => c.Number is null && c.Volume is null).ToList();
        Assert.Equal(
            new[] { "~ love panic ~ (10th Anniversary of Yuri Hime)", "Harumin's Sleepover Party" },
            extras.Select(c => c.NumberRaw));
    }

    [Fact]
    public async Task ListChapters_keeps_every_permalinked_entry()
    {
        Assert.Equal(60, (await WithSeries().ListChaptersAsync("citrus")).Count);
        Assert.Equal(250, (await WithLongSeries().ListChaptersAsync("yuru_yuri")).Count);
    }

    [Fact]
    public async Task ListChapters_does_not_read_a_decimal_volume_header_as_the_whole_volume()
    {
        var chapters = await WithLongSeries().ListChaptersAsync("yuru_yuri");

        // Intermission.17 to .24 sit under a "Volume 10.5" header, right after volume 10's "Bonus Track 10".
        var headers = Enumerable.Range(17, 8).Select(n => $"Intermission.{n}:").ToList();
        var intermissions = chapters.Where(c => headers.Any(h => c.NumberRaw!.StartsWith(h, StringComparison.Ordinal))).ToList();
        Assert.Equal(8, intermissions.Count);
        Assert.All(intermissions, c => Assert.Null(c.Volume));
        Assert.Equal(
            "yuru_yuri_bonus_track_10_another_business_trip_to_comic_rex",
            Assert.Single(chapters, c => c.Number is null && c.Volume == 10).SourceChapterId);
    }

    [Fact]
    public async Task ListChapters_gives_colon_less_specials_in_different_volumes_distinct_titles()
    {
        // ChapterIdentity.Matches identifies a null-number chapter by (IsOneShot, Language,
        // Title) alone, ignoring Volume. A null Title here would alias "Vol. 1 Special" and
        // "Vol. 2 Special" (and every other colon-less special) into the same chapter row on
        // sync.
        var chapters = await WithSeries().ListChaptersAsync("citrus");

        var vol1Special = Assert.Single(chapters, c => c.Number is null && c.Volume == 1);
        var vol2Special = Assert.Single(chapters, c => c.Number is null && c.Volume == 2);

        Assert.Equal("Vol. 1 Special", vol1Special.Title);
        Assert.Equal("Vol. 2 Special", vol2Special.Title);
        Assert.NotEqual(vol1Special.Title, vol2Special.Title);
        Assert.All(chapters.Where(c => c.Number is null), c => Assert.NotNull(c.Title));
    }

    [Fact]
    public async Task ListChapters_still_leaves_a_colon_less_numbered_chapter_title_null()
    {
        // Numbered chapters are looked up by (Number, Volume, Language), never by Title, so the
        // colon-less fallback that null-number chapters need must not apply to them.
        var source = SourceFor(new()
        {
            ["series/no-colon.json"] = """
                {
                  "name": "No Colon", "type": "Series", "permalink": "no-colon", "tags": [],
                  "taggings": [
                    { "header": "Volume 1" },
                    { "title": "Chapter 5", "permalink": "no_colon_ch05", "released_on": "2020-01-01", "tags": [] }
                  ]
                }
                """
        });

        var chapters = await source.ListChaptersAsync("no-colon");

        var chapter = Assert.Single(chapters);
        Assert.Equal(5m, chapter.Number);
        Assert.Null(chapter.Title);
    }

    [Fact]
    public async Task ListChapters_parses_a_dotted_chapter_number_from_a_long_series()
    {
        var chapters = await WithLongSeries().ListChaptersAsync("yuru_yuri");

        var half = chapters.Single(c => c.SourceChapterId == "yuru_yuri_ch8_5");
        Assert.Equal(8.5m, half.Number);
        Assert.Equal("She Was...Right Behind Me", half.Title);
    }

    [Fact]
    public async Task GetPages_reads_the_page_list_with_referer_header()
    {
        var source = SourceFor(new()
        {
            ["chapters/citrus_ch01.json"] = FakeHttpClientFactory.Fixture("dynasty-chapter.json")
        });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "dynasty", "citrus", "citrus_ch01", "Chapter 1: love affair!?", 1m, 1, null, "en", null));

        Assert.Equal(37, pages.Pages.Count);
        Assert.Equal(
            "https://dynasty-scans.com/system/releases/000/005/265/citrus_ch01_01.webp", pages.Pages[0].Url);
        // A spread page ("02-03") is just another page url; no special handling needed.
        Assert.Equal(
            "https://dynasty-scans.com/system/releases/000/005/265/citrus_ch01_02-03.webp", pages.Pages[1].Url);
        Assert.All(pages.Pages, p => Assert.Equal("https://dynasty-scans.com/", p.Headers!["Referer"]));
    }
}
