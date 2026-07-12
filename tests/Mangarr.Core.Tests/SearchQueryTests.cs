using Mangarr.Core.Indexers;

namespace Mangarr.Core.Tests;

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
}
