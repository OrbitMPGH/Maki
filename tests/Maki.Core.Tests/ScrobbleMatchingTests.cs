using Maki.Core.Scrobbling;

namespace Maki.Core.Tests;

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

    [Theory]
    // A short, generic title is a fragment of any longer title built out of the same words, and it
    // scores *higher* there (0.65 / 0.71) than a real subtitle variant does (0.64) - so no threshold
    // can separate them, and word coverage can't either: it divides by the shorter title's word
    // count, which a contained title always covers completely.
    [InlineData("She's Adopted a High School Boy!")]
    [InlineData("Magic, High School, and a Boy")]
    public void BestCandidateRejectsATitleItIsOnlyAFragmentOf(string candidateTitle)
    {
        var candidates = new List<ScrobbleCandidate> { new("1", candidateTitle, [], "") };

        Assert.Null(ScrobbleMatching.BestCandidate("High School Boy", null, candidates, 0.6));
    }

    [Fact]
    public void BestCandidateStillAcceptsAnAppendedSubtitle()
    {
        // The case the low source-matching threshold exists for, and the one the fragment rule has
        // to keep: the extra words come after the whole of our title, not around it.
        var candidates = new List<ScrobbleCandidate>
        {
            new("1", "Hajime no Ippo: Fighting Spirit!", [], ""),
        };

        Assert.Equal("1", ScrobbleMatching.BestCandidate("Hajime no Ippo", null, candidates, 0.6)?.Id);
    }

    [Fact]
    public void BestCandidateAcceptsTheShorterFormToo()
    {
        // Which of the two forms we hold is MangaBaka's choice, so the source is as likely to be
        // listing the shorter one.
        var candidates = new List<ScrobbleCandidate> { new("1", "Hajime no Ippo", [], "") };

        Assert.Equal(
            "1",
            ScrobbleMatching.BestCandidate("Hajime no Ippo: Fighting Spirit!", null, candidates, 0.6)?.Id);
    }

    [Fact]
    public void BestCandidateStillRejectsATitleExtendedTooFar()
    {
        // "Naruto" extends to this the same way, so shape alone would let it through - the score is
        // what rules it out, at 0.32.
        var candidates = new List<ScrobbleCandidate>
        {
            new("1", "Naruto Gaiden: The Seventh Hokage", [], ""),
        };

        Assert.Null(ScrobbleMatching.BestCandidate("Naruto", null, candidates, 0.6));
    }

    [Fact]
    public void BestCandidateAcceptsAStrongMatchThatIsNotAnExtension()
    {
        // Punctuation and dropped articles land mid-title, so they are not extensions of anything -
        // they don't need to be, because they score high enough to stand on their own.
        var spyFamily = new List<ScrobbleCandidate> { new("1", "Spy Family", [], "") };
        var shieldHero = new List<ScrobbleCandidate> { new("2", "Rising of the Shield Hero", [], "") };

        Assert.Equal("1", ScrobbleMatching.BestCandidate("Spy x Family", null, spyFamily, 0.6)?.Id);
        Assert.Equal(
            "2",
            ScrobbleMatching.BestCandidate("The Rising of the Shield Hero", null, shieldHero, 0.6)?.Id);
    }

    [Fact]
    public void BestCandidateWordBoundaryStopsAnExtensionMidWord()
    {
        // "Blue Locker" starts with "Blue Lock" as characters but is not that title plus a word.
        var candidates = new List<ScrobbleCandidate> { new("1", "Blue Locker", [], "") };

        Assert.Null(ScrobbleMatching.BestCandidate("Blue Lock", null, candidates, 0.6));
    }

    [Fact]
    public void BestCandidateLeavesAStricterCallerAlone()
    {
        // The scrobbler runs at 0.93, above the standalone bar, so the shape relaxation must not
        // hand it anything its own threshold would have refused.
        var candidates = new List<ScrobbleCandidate> { new("1", "Spy Family", [], "") };

        Assert.Null(ScrobbleMatching.BestCandidate("Spy x Family", null, candidates));
    }

    [Fact]
    public void BestCandidateHandlesEmptyList()
    {
        Assert.Null(ScrobbleMatching.BestCandidate("Anything", null, []));
    }

    [Fact]
    public void BestCandidateRejectsSharedPrefixWithSwappedWord()
    {
        // Regression: "Boy Meets Maria" vs "Boy Meets Girl (Wone)" scores ~0.7 on char
        // similarity alone (shared "boy meets " prefix), well above a low threshold like
        // SourceMatchService's 0.6, despite being an unrelated title.
        var candidates = new List<ScrobbleCandidate>
        {
            new("1", "Boy Meets Girl (Wone)", [], ""),
        };

        Assert.Null(ScrobbleMatching.BestCandidate("Boy Meets Maria", null, candidates, threshold: 0.6));
    }

    [Fact]
    public void BestCandidateAcceptsAppendedSubtitleAtLowThreshold()
    {
        // The word-coverage guard must not break the legitimate low-threshold subtitle
        // case it was designed to allow.
        var candidates = new List<ScrobbleCandidate>
        {
            new("1", "Hajime no Ippo: Fighting Spirit!", [], ""),
        };

        var best = ScrobbleMatching.BestCandidate("Hajime no Ippo", null, candidates, threshold: 0.6);
        Assert.Equal("1", best?.Id);
    }
}
