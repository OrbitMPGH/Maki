using AngleSharp.Html.Parser;
using Maki.Core.Sources;
using Maki.Sources.AnimeSama;

namespace Maki.Sources.Tests;

public class AnimeSamaSourceTests
{
    private static readonly HtmlParser Parser = new();

    [Fact]
    public async Task Search_parses_results()
    {
        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/?type"] = FakeHttpClientFactory.Fixture("animesama-search.html")
        }));

        var results = await source.SearchAsync("one piece");

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("one-piece", first.SourceSeriesId);
        Assert.Equal("One Piece", first.Title);
        Assert.Equal("https://anime-sama.to/catalogue/one-piece/", first.Url);
        Assert.StartsWith("https://cdn.jsdelivr.net", first.CoverUrl);
    }

    [Fact]
    public async Task GetSeries_parses_detail()
    {
        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/one-piece/"] = FakeHttpClientFactory.Fixture("animesama-series.html")
        }));

        var detail = await source.GetSeriesAsync("one-piece");

        Assert.Equal("one-piece", detail.SourceSeriesId);
        Assert.Equal("One Piece", detail.Title);
        Assert.Equal("En cours", detail.Status);
        Assert.StartsWith("https://cdn.jsdelivr.net", detail.CoverUrl);
        Assert.Contains("Gold Roger", detail.Description);
    }

    [Fact]
    public async Task ListChapters_parses_bw_labels_and_normalizes_ascending()
    {
        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/one-piece/scan_noir-et-blanc/vf/"] = FakeHttpClientFactory.Fixture("animesama-scan.html"),
            ["get_nb_chap_et_img.php"] = FakeHttpClientFactory.Fixture("animesama-chapters.json")
        }));

        var chapters = await source.ListChaptersAsync("one-piece/scan_noir-et-blanc/vf");

        // 1194 positions total; SourceChapterList.Normalize sorts ascending by Number and puts the
        // single null-Number "One Shot" chapter first.
        Assert.Equal(1194, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("fr", c.Language));

        var oneShot = chapters[0];
        Assert.Null(oneShot.Number);
        Assert.Equal("One Shot", oneShot.NumberRaw);
        Assert.Equal("one-piece/scan_noir-et-blanc/vf|One Shot", oneShot.SourceChapterId);

        var last = chapters[^1];
        Assert.Equal(1193m, last.Number);
        Assert.Equal("1193", last.NumberRaw);
        Assert.Equal("one-piece/scan_noir-et-blanc/vf|1193", last.SourceChapterId);

        var first = chapters[1];
        Assert.Equal(1m, first.Number);
    }

    [Fact]
    public async Task ListChapters_throws_on_error_json()
    {
        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/some-slug/scan/vf/"] = FakeHttpClientFactory.Fixture("animesama-scan-notfound.html"),
            ["get_nb_chap_et_img.php"] = FakeHttpClientFactory.Fixture("animesama-chapters-error.json")
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ListChaptersAsync("some-slug/scan/vf"));
    }

    [Fact]
    public async Task GetPages_for_label_1193_points_at_position_1194()
    {
        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/one-piece/scan_noir-et-blanc/vf/"] = FakeHttpClientFactory.Fixture("animesama-scan.html"),
            ["get_nb_chap_et_img.php"] = FakeHttpClientFactory.Fixture("animesama-chapters.json")
        }));

        var chapter = new SourceChapter(
            "animesama", "one-piece/scan_noir-et-blanc/vf", "one-piece/scan_noir-et-blanc/vf|1193",
            "1193", 1193, null, null, "fr", null);

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(18, pages.Pages.Count);
        var page = pages.Pages[0];
        Assert.Equal("https://anime-sama.to/s2/scans/One%20Piece/1194/1.jpg", page.Url);
        Assert.Equal("https://anime-sama.to/", page.Headers!["Referer"]);
    }

    [Fact]
    public void FirstScanPanel_skips_template_and_returns_first_real_panel()
    {
        var html = FakeHttpClientFactory.Fixture("animesama-series.html");
        Assert.Equal("scan/vf", AnimeSamaSource.FirstScanPanel(html));
    }

    [Fact]
    public async Task BuildLabels_handles_creerListe_newSP_and_finirListe()
    {
        var html = FakeHttpClientFactory.Fixture("animesama-scan.html");
        var doc = await Parser.ParseDocumentAsync(html);

        var labels = AnimeSamaSource.BuildLabels(doc, 1194);

        Assert.Equal(1194, labels.Count);
        Assert.Equal("1", labels[0]);
        Assert.Equal("1045", labels[1044]);
        Assert.Equal("One Shot", labels[1045]);
        Assert.Equal("1046", labels[1046]);
        Assert.Equal("1193", labels[1193]);
    }

    [Fact]
    public async Task BuildLabels_falls_back_to_1_through_total_without_a_script()
    {
        var html = FakeHttpClientFactory.Fixture("animesama-scan-plain.html");
        var doc = await Parser.ParseDocumentAsync(html);

        var labels = AnimeSamaSource.BuildLabels(doc, 1004);

        Assert.Equal(1004, labels.Count);
        Assert.Equal("1", labels[0]);
        Assert.Equal("1004", labels[^1]);
    }

    [Fact]
    public async Task BuildLabels_handles_newSP_zero_like_solo_leveling()
    {
        const string html = """
            <html><body>
            <script>
            $(document).ready(function(){
                /*
                resetListe(); toujours au debut en premier
                creerListe(debut, fin);newSP(special); on en met autant qu'on veut
                finirListe(debut de la fin);
                */
                resetListe();newSP(0);
                finirListe(1);
            });
            </script>
            </body></html>
            """;
        var doc = await Parser.ParseDocumentAsync(html);

        var labels = AnimeSamaSource.BuildLabels(doc, 202);

        Assert.Equal(202, labels.Count);
        Assert.Equal("0", labels[0]);
        Assert.Equal("1", labels[1]);
        Assert.Equal("201", labels[^1]);
    }
}
