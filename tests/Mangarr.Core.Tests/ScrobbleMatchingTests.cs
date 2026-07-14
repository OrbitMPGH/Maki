using Mangarr.Core.Scrobbling;

namespace Mangarr.Core.Tests;

public class ScrobbleMatchingTests
{
    [Fact]
    public void ParsesWebLinks()
    {
        var ids = ScrobbleMatching.ParseWebLinks([
            "https://anilist.co/manga/30013",
            "https://myanimelist.net/manga/13",
            "https://mangabaka.org/8215",
        ]);

        Assert.Equal("30013", ids["anilist"]);
        Assert.Equal("13", ids["mal"]);
        Assert.Equal("8215", ids["mangabaka"]);
    }

    [Theory]
    [InlineData("https://mangabaka.org/8215", "8215")]         // no /series/ segment
    [InlineData("https://mangabaka.dev/8215", "8215")]  // .dev domain
    public void ParsesMangaBakaLinkVariants(string url, string expected)
    {
        Assert.Equal(expected, ScrobbleMatching.ParseWebLinks([url])["mangabaka"]);
    }

    [Fact]
    public void FirstLinkPerServiceWins()
    {
        var ids = ScrobbleMatching.ParseWebLinks([
            "https://anilist.co/manga/1",
            "https://anilist.co/manga/2",
        ]);

        Assert.Equal("1", ids["anilist"]);
    }

    [Fact]
    public void IgnoresUnrelatedLinks()
    {
        Assert.Empty(ScrobbleMatching.ParseWebLinks(["https://mangadex.org/title/abc", "not a url"]));
    }

    [Theory]
    [InlineData("Hajime no Ippo: Fighting Spirit!", "hajime no ippo fighting spirit")]
    [InlineData("  Frieren – Beyond   Journey's End ", "frieren beyond journey s end")]
    public void NormalizesTitles(string input, string expected)
    {
        Assert.Equal(expected, ScrobbleMatching.NormalizeTitle(input));
    }

    [Fact]
    public void IdenticalTitlesScoreOne()
    {
        // punctuation/case differences vanish in normalization
        Assert.Equal(1.0, ScrobbleMatching.TitleSimilarity("Hajime no Ippo!", "hajime no ippo"));
    }

    [Fact]
    public void SimilarTitlesScoreHigh()
    {
        Assert.True(ScrobbleMatching.TitleSimilarity(
            "Sono Bisque Doll wa Koi wo Suru",
            "Sono Bisque Doll ha Koi wo Suru") > 0.93);
    }

    [Fact]
    public void DifferentTitlesScoreLow()
    {
        Assert.True(ScrobbleMatching.TitleSimilarity("One Piece", "Berserk") < 0.5);
    }

    [Fact]
    public void BestCandidateAcceptsCloseMatch()
    {
        var candidates = new List<ScrobbleCandidate>
        {
            new("1", "Some Other Manga", [], ""),
            new("2", "Hajime no Ippo", ["Fighting Spirit"], ""),
        };

        var best = ScrobbleMatching.BestCandidate("Hajime no Ippo", null, candidates);
        Assert.Equal("2", best?.Id);
    }

    [Fact]
    public void BestCandidateMatchesOnAltTitles()
    {
        var candidates = new List<ScrobbleCandidate>
        {
            new("7", "その着せ替え人形は恋をする", ["My Dress-Up Darling"], ""),
        };

        var best = ScrobbleMatching.BestCandidate("My Dress-Up Darling", null, candidates);
        Assert.Equal("7", best?.Id);
    }

    [Fact]
    public void BestCandidateMatchesOnQueryAltTitle()
    {
        var candidates = new List<ScrobbleCandidate>
        {
            new("9", "Sousou no Frieren", [], ""),
        };

        var best = ScrobbleMatching.BestCandidate(
            "Frieren: Beyond Journey's End", "Sousou no Frieren", candidates);
        Assert.Equal("9", best?.Id);
    }

    [Fact]
    public void BestCandidateRejectsBelowThreshold()
    {
        var candidates = new List<ScrobbleCandidate>
        {
            new("1", "Hajime no Ippo Gaiden", [], ""), // related but different series
        };

        Assert.Null(ScrobbleMatching.BestCandidate("Hajime no Ippo", null, candidates));
    }

    [Fact]
    public void BestCandidateHandlesEmptyList()
    {
        Assert.Null(ScrobbleMatching.BestCandidate("Anything", null, []));
    }
}
