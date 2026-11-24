using System.Text.Json;
using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class ChapterPageMatchTests
{
    // The 2026-11-17 page audit: 42 merged floors page counts nominated, with every page's hash and the
    // verdict from the contact sheets (31 right, 11 wrong: extras, bonus chapters, another series).
    private static readonly Lazy<List<AuditCase>> Audit = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chapter-page-audit.json")));
        return [.. doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new AuditCase(
            c.GetProperty("series").GetInt32(),
            $"{c.GetProperty("title").GetString()} {c.GetProperty("floor").GetRawText()}",
            c.GetProperty("label").GetString() == "right",
            [.. c.GetProperty("parts").EnumerateArray().Select(p => ChapterPageMatch.Parse(p.GetString())!)],
            [.. c.GetProperty("wholes").EnumerateObject().Select(w => ChapterPageMatch.Parse(w.Value.GetString())!)]))];
    });

    private sealed record AuditCase(int Series, string Name, bool Right, List<IReadOnlyList<ulong>> Parts, List<IReadOnlyList<ulong>> Wholes)
    {
        public bool Matches => Wholes.Any(w => ChapterPageMatch.Compare(w, Parts).Verdict == PageMatchVerdict.Match);
    }

    [Fact]
    public void NoWrongMergeOfThePageAuditMatches()
    {
        Assert.Equal(11, Audit.Value.Count(c => !c.Right));
        Assert.Empty(Audit.Value.Where(c => !c.Right && c.Matches).Select(c => c.Name));
    }

    [Fact]
    public void MostRightMergesOfThePageAuditMatch()
    {
        // The six it misses are recut webtoon strips, an official volume against a fan scan, and
        // MangaKakalot's extra wide strip per page: they keep a duplicate row, the safe side.
        var missed = Audit.Value.Where(c => c.Right && !c.Matches).Select(c => c.Name).ToList();
        Assert.Equal(31, Audit.Value.Count(c => c.Right));
        Assert.Equal(6, missed.Count);
    }

    [Fact]
    public void PartsNeverMatchAnotherSeriesWhole()
    {
        var accepted = (
            from parts in Audit.Value
            from other in Audit.Value
            where parts.Series != other.Series
            from whole in other.Wholes
            where ChapterPageMatch.Compare(whole, parts.Parts).Verdict == PageMatchVerdict.Match
            select $"{parts.Name} in {other.Name}").ToList();

        Assert.Empty(accepted);
    }

    private static ulong Page(int seed)
    {
        // A detailed page: about half its bits set, different for every seed.
        var random = new Random(seed);
        ulong hash;
        do
        {
            hash = (ulong)random.NextInt64();
        }
        while (System.Numerics.BitOperations.PopCount(hash) is < 24 or > 40);

        return hash;
    }

    private static List<ulong> Pages(int from, int count) => [.. Enumerable.Range(from, count).Select(Page)];

    [Fact]
    public void PartsInOrderMatchTheirWhole()
    {
        var whole = Pages(0, 20);

        var result = ChapterPageMatch.Compare(whole, [whole[..10], whole[10..]]);

        Assert.Equal(PageMatchVerdict.Match, result.Verdict);
        Assert.Equal([1.0, 1.0], result.Shares);
        Assert.Equal(1.0, result.Cover);
    }

    [Fact]
    public void OneWholePageCannotVouchForSeveral()
    {
        // Nagatoro 46: near-identical pages across both parts all found the same page of an unrelated chapter.
        var whole = Pages(0, 20);
        var repeated = Enumerable.Repeat(whole[5], 6).ToList();

        Assert.NotEqual(PageMatchVerdict.Match, ChapterPageMatch.Compare(whole, [repeated, repeated]).Verdict);
    }

    [Fact]
    public void NearBlankPagesAreNoEvidence()
    {
        var blank = new List<ulong> { 0, 0x0000000000080808, 0, 0x10, 0 };

        Assert.Equal(PageMatchVerdict.Unsure, ChapterPageMatch.Compare(blank, [blank, blank]).Verdict);
    }

    [Fact]
    public void APartWithPagesTheWholeLacksDoesNotMatch()
    {
        // The chapter's second half plus a bonus of its own: the share passes, the unmatched run does not.
        var whole = Pages(0, 20);
        var second = whole[10..].Concat(Pages(100, 8)).ToList();

        var result = ChapterPageMatch.Compare(whole, [whole[..10], second]);

        Assert.Equal(PageMatchVerdict.Differ, result.Verdict);
        Assert.True(result.Shares[1] >= ChapterPageMatch.MinShare);
    }

    [Fact]
    public void PartsOutOfOrderDoNotMatch()
    {
        var whole = Pages(0, 20);

        Assert.Equal(PageMatchVerdict.Differ, ChapterPageMatch.Compare(whole, [whole[10..], whole[..10]]).Verdict);
    }

    [Fact]
    public void HashesRoundTrip()
    {
        var hashes = Pages(0, 5);

        Assert.Equal(hashes, ChapterPageMatch.Parse(ChapterPageMatch.Format(hashes)));
        Assert.Null(ChapterPageMatch.Parse("abc"));
        Assert.Null(ChapterPageMatch.Parse(null));
    }
}
