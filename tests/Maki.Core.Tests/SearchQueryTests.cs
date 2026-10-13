using Maki.Core.Entities;
using Maki.Core.Indexers;

namespace Maki.Core.Tests;

public class SearchQueryTests
{
    [Fact]
    public void SubtitleAfterColonFallsBackToMainTitle()
    {
        var candidates = SearchQuery.Candidates("Ima Koi: Now I’m In Love").ToList();

        Assert.Equal(["Ima Koi: Now I'm In Love", "Ima Koi"], candidates);
    }

    [Fact]
    public void PlainTitleHasSingleCandidate()
    {
        Assert.Equal(["Dandadan"], SearchQuery.Candidates("Dandadan").ToList());
    }

    [Fact]
    public void CurlyPunctuationIsNormalized()
    {
        Assert.Equal(["Komi Can't Communicate"], SearchQuery.Candidates("Komi Can’t Communicate").ToList());
    }

    [Fact]
    public void SpacedDashIsASubtitleSeparator()
    {
        var candidates = SearchQuery.Candidates("Frieren - Beyond Journey's End").ToList();

        Assert.Equal(["Frieren - Beyond Journey's End", "Frieren"], candidates);
    }

    [Fact]
    public void HyphenInsideWordIsNotASeparator()
    {
        Assert.Equal(["Re-Monster"], SearchQuery.Candidates("Re-Monster").ToList());
    }

    [Fact]
    public void EarliestSeparatorWins()
    {
        var candidates = SearchQuery.Candidates("Ima Koi: Now - Extra").ToList();

        Assert.Equal(["Ima Koi: Now - Extra", "Ima Koi"], candidates);
    }

    [Fact]
    public void SingleCharMainTitleIsSkipped()
    {
        // "K: Return of Kings" style — a 1-char query would match everything.
        Assert.Equal(["K: Something"], SearchQuery.Candidates("K: Something").ToList());
    }

    [Fact]
    public void WhitespaceIsCollapsed()
    {
        Assert.Equal(["Spy x Family"], SearchQuery.Candidates("  Spy  x   Family ").ToList());
    }

    [Fact]
    public void FallbacksPreferEnglishThenRomanizedThenOtherLatinThenTheRest()
    {
        var queries = SearchQuery.WithFallbacks("Main", "進撃の巨人伝", [
            new LocalizedTitle("Shingeki no Kyojin", "ja-Latn"),
            new LocalizedTitle("L'Attaque des Titans", "fr"),
            new LocalizedTitle("Attack on Titan", "en"),
            new LocalizedTitle("巨人伝", "ja")
        ], maxExtra: 10);

        Assert.Equal(
            ["Main", "Attack on Titan", "Shingeki no Kyojin", "L'Attaque des Titans", "進撃の巨人伝", "巨人伝"],
            queries);
    }

    [Fact]
    public void FallbacksAreCappedAtTheExtraLimit()
    {
        var alts = Enumerable.Range(1, 8).Select(i => new LocalizedTitle($"Alt {i}", "en")).ToList();

        var queries = SearchQuery.WithFallbacks("Main", "Original", alts);

        Assert.Equal(4, queries.Count);
        Assert.Equal(["Main", "Alt 1", "Alt 2", "Alt 3"], queries);
    }

    [Fact]
    public void FallbacksDropDuplicatesAndTinyTitles()
    {
        var queries = SearchQuery.WithFallbacks("Dandadan", "dandadan", [
            new LocalizedTitle("DANDADAN", "en"),
            new LocalizedTitle("DD", "en"),
            new LocalizedTitle("  ", "en"),
            new LocalizedTitle("Dan Da Dan", "en")
        ]);

        Assert.Equal(["Dandadan", "Dan Da Dan"], queries);
    }

    [Fact]
    public void FallbacksKeepTheSubtitleSplitOfTheMainTitleFirst()
    {
        var queries = SearchQuery.WithFallbacks("Ima Koi: Now I’m In Love", null, [new LocalizedTitle("Now I'm in Love", "en")]);

        Assert.Equal(["Ima Koi: Now I'm In Love", "Ima Koi", "Now I'm in Love"], queries);
    }
}
