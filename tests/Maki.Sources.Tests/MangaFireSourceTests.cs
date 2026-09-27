using Maki.Sources.MangaFire;

namespace Maki.Sources.Tests;

/// <summary>
/// MangaFireSource.ListChaptersAsync drives everything through MangaFireBrowser, a sealed class
/// that owns a real headless Chromium and can't be faked in a unit test. The filtering/mapping
/// logic that decides what an item's language is (MangaFireSource.BuildChapters) is pure once the
/// browser has handed back its raw items and whether the language switch it attempted succeeded,
/// so it's tested directly against hand-built item JSON instead.
/// </summary>
public class MangaFireSourceTests
{
    private static string Item(string id, decimal number, string? language) =>
        language is null
            ? $$"""{"id":{{id}},"number":{{number}},"name":"Ch. {{number}}","type":"official"}"""
            : $$"""{"id":{{id}},"number":{{number}},"name":"Ch. {{number}}","type":"official","language":"{{language}}"}""";

    [Fact]
    public void BuildChapters_labels_unlabelled_items_with_the_requested_language_when_the_switch_matched()
    {
        var chapters = MangaFireSource.BuildChapters(
            "7wypj-some-slug", [Item("1", 1m, null)], ["es"]);

        var chapter = Assert.Single(chapters);
        Assert.Equal("es", chapter.Language);
    }

    [Fact]
    public void EnsureLanguageMatched_throws_when_the_switch_failed()
    {
        // The switch to "es" failed, so the browser is still showing whatever language was loaded
        // before the attempt - not necessarily es. Returning [] here would read to
        // ChapterSyncService as a clean empty snapshot and delete every ChapterSourceLink, so
        // ListChaptersAsync must throw instead of ever reaching BuildChapters.
        var ex = Assert.Throws<InvalidOperationException>(
            () => MangaFireSource.EnsureLanguageMatched(languageMatched: false, requested: "es"));

        Assert.Contains("es", ex.Message);
    }

    [Fact]
    public void EnsureLanguageMatched_does_not_throw_when_the_switch_matched()
    {
        MangaFireSource.EnsureLanguageMatched(languageMatched: true, requested: "es");
    }

    [Fact]
    public void BuildChapters_drops_unlabelled_items_when_several_languages_were_requested()
    {
        var chapters = MangaFireSource.BuildChapters(
            "7wypj-some-slug", [Item("1", 1m, null)], ["en", "es"]);

        Assert.Empty(chapters);
    }

    [Fact]
    public void BuildChapters_keeps_a_labelled_item()
    {
        var chapters = MangaFireSource.BuildChapters(
            "7wypj-some-slug", [Item("1", 1m, "es")], ["es"]);

        var chapter = Assert.Single(chapters);
        Assert.Equal("es", chapter.Language);
    }

    [Fact]
    public void BuildChapters_drops_a_labelled_item_whose_language_was_not_requested()
    {
        var chapters = MangaFireSource.BuildChapters(
            "7wypj-some-slug", [Item("1", 1m, "fr")], ["es"]);

        Assert.Empty(chapters);
    }

    [Fact]
    public void BuildChapters_prefers_the_official_release_when_both_exist_for_the_same_number()
    {
        const string unofficial = """{"id":1,"number":5,"name":"Ch. 5 (fan)","type":"fan"}""";
        const string official = """{"id":2,"number":5,"name":"Ch. 5","type":"official"}""";

        var chapters = MangaFireSource.BuildChapters(
            "7wypj-some-slug", [unofficial, official], ["en"]);

        var chapter = Assert.Single(chapters);
        Assert.Equal("2", chapter.SourceChapterId);
    }
}
