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
        // Null-number identity (ChapterIdentity.Matches) is IsOneShot + Language + Title, so the
        // special needs its own Title or a differently-named special would collide with it.
        Assert.Equal("One Shot", oneShot.Title);

        var last = chapters[^1];
        Assert.Equal(1193m, last.Number);
        Assert.Equal("1193", last.NumberRaw);
        Assert.Equal("one-piece/scan_noir-et-blanc/vf|1193", last.SourceChapterId);
        Assert.Null(last.Title);

        var first = chapters[1];
        Assert.Equal(1m, first.Number);
        Assert.Null(first.Title);
    }

    [Fact]
    public async Task ListChapters_keeps_every_text_special()
    {
        const string html = """
            <html><body>
            <h3 id="titreOeuvre">Foo</h3>
            <script>
            $(document).ready(function(){
                resetListe();
                creerListe(1, 2);newSP("Bonus A");newSP("Bonus B");
                finirListe(3);
            });
            </script>
            </body></html>
            """;
        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/foo/scan/vf/"] = html,
            ["get_nb_chap_et_img.php"] = """{"1":5,"2":5,"3":5,"4":5,"5":5}"""
        }));

        var chapters = await source.ListChaptersAsync("foo/scan/vf");

        Assert.Equal(5, chapters.Count);
        Assert.Equal(
            new[] { "Bonus A", "Bonus B" },
            chapters.Where(c => c.Number is null).Select(c => c.Title));
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
    public async Task ListChapters_retries_the_count_endpoint_with_amp_escaped_ampersand()
    {
        // #titreOeuvre decodes to "Foo & Bar" (AngleSharp un-escapes the &amp; in the fixture), so the
        // first attempt asks for that raw text and only the &amp;-escaped retry succeeds - mirroring
        // the site's own script, which reads the name off innerHTML (re-escaped) rather than the
        // decoded text.
        const string rawAttemptError = """{"error":"Oeuvre 'Foo & Bar' not found"}""";
        const string escapedAttemptCounts = """{"1":5,"2":6,"3":7}""";

        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/foo-bar/scan/vf/"] = FakeHttpClientFactory.Fixture("animesama-scan-ampersand.html"),
            ["oeuvre=Foo%20%26%20Bar"] = rawAttemptError,
            ["oeuvre=Foo%20%26amp%3B%20Bar"] = escapedAttemptCounts
        }));

        var chapters = await source.ListChaptersAsync("foo-bar/scan/vf");

        Assert.Equal(3, chapters.Count);
    }

    [Fact]
    public async Task ListChapters_throws_when_both_ampersand_forms_come_back_not_found()
    {
        const string notFoundEither = """{"error":"Oeuvre 'Foo & Bar' not found"}""";

        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/foo-bar/scan/vf/"] = FakeHttpClientFactory.Fixture("animesama-scan-ampersand.html"),
            ["get_nb_chap_et_img.php"] = notFoundEither
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ListChaptersAsync("foo-bar/scan/vf"));
    }

    [Fact]
    public async Task GetPages_throws_ChapterLocked_when_position_has_zero_pages()
    {
        // The plain-panel fixture (no active list script) falls back to labels 1..total, so a
        // one-entry counts JSON trivially satisfies BuildLabels' total check without needing a
        // real 1194-position chapter list here.
        var source = new AnimeSamaSource(new FakeHtmlFetcher(new()
        {
            ["/catalogue/one-piece/scan/vf/"] = FakeHttpClientFactory.Fixture("animesama-scan-plain.html"),
            ["get_nb_chap_et_img.php"] = """{"1":0}"""
        }));

        var chapter = new SourceChapter(
            "animesama", "one-piece/scan/vf", "one-piece/scan/vf|1",
            "1", 1, null, null, "fr", null);

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(chapter));
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
