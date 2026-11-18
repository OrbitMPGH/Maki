using System.Text.Json;
using Maki.Core.Sources;
using Xunit.Abstractions;

namespace Maki.Core.Tests;

public class ChapterStripMatchTests(ITestOutputHelper output)
{
    // The labelled floors of the 2026-11-17 page audit, its blind check and TopManhua's Dress-Up Darling 3,
    // each chapter as StripHash makes it from the cached 120 px page thumbnails.
    private static readonly Lazy<List<StripCase>> Audit = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chapter-strip-audit.json")));
        return [.. doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new StripCase(
            c.GetProperty("series").GetInt32(),
            c.GetProperty("name").GetString()!,
            c.GetProperty("label").GetString() == "right",
            [.. c.GetProperty("parts").EnumerateArray().Select(p => ChapterPageMatch.ParseStrip(p.GetString())!)],
            [.. c.GetProperty("wholes").EnumerateObject().Select(w => ChapterPageMatch.ParseStrip(w.Value.GetString())!)]))];
    });

    private sealed record StripCase(int Series, string Name, bool Right, List<IReadOnlyList<ulong>> Parts, List<IReadOnlyList<ulong>> Wholes)
    {
        public bool Matches => Wholes.Any(w => ChapterPageMatch.CompareStrips(w, Parts).Verdict == PageMatchVerdict.Match);
    }

    [Fact]
    public void NoWrongMergeMatchesAsAStrip()
    {
        Assert.Equal(8, Audit.Value.Count(c => !c.Right));
        Assert.Empty(Audit.Value.Where(c => !c.Right && c.Matches).Select(c => c.Name));
    }

    [Fact]
    public void StripsFindTheChaptersPagesMiss()
    {
        // A webtoon cut at other points, MangaKakalot's page plus crop, and TopManhua's pages cut in two.
        string[] cut = ["The Dark Mage's Return to Enlistment 48", "Too Many Losing Heroines! 10", "Too Many Losing Heroines! 22",
            "My Dress-Up Darling 3 (TopManhua slices)"];
        foreach (var name in cut)
        {
            Assert.True(Audit.Value.Single(c => c.Name.StartsWith(name, StringComparison.Ordinal)).Matches, name);
        }

        var missed = Audit.Value.Where(c => c.Right && !c.Matches).Select(c => c.Name).ToList();
        output.WriteLine($"{Audit.Value.Count(c => c.Right) - missed.Count} of {Audit.Value.Count(c => c.Right)} right floors match: missed {string.Join("; ", missed)}");
        // Left: three different scans (Hidden Dungeon 17, Komi 196, Now That We Draw 19), and Dress-Up
        // Darling 1, which its pages already match.
        Assert.Equal(4, missed.Count);
    }

    [Fact]
    public void StripsNeverMatchAnotherFloorOrSeries()
    {
        var accepted = (
            from parts in Audit.Value
            from other in Audit.Value
            where !ReferenceEquals(parts, other)
            from whole in other.Wholes
            where ChapterPageMatch.CompareStrips(whole, parts.Parts).Verdict == PageMatchVerdict.Match
            select $"{parts.Name} in {other.Name}").ToList();

        Assert.Empty(accepted);
    }

    [Fact]
    public void StripsRoundTrip()
    {
        ulong[] windows = [0, 1, ulong.MaxValue, 0x0123456789abcdef];

        Assert.Equal(windows, ChapterPageMatch.ParseStrip(ChapterPageMatch.FormatStrip(windows)));
        Assert.Null(ChapterPageMatch.ParseStrip("not base64!"));
        Assert.Empty(ChapterPageMatch.ParseStrip(ChapterPageMatch.FormatStrip([]))!);
    }
}
