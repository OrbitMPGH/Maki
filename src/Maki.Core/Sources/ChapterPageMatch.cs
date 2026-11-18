using System.Globalization;
using System.Numerics;
using System.Text;

namespace Maki.Core.Sources;

public enum PageMatchVerdict
{
    /// <summary>Every part's pages are in the whole, in reading order, and they make up most of it.</summary>
    Match,

    /// <summary>Compared, and the pages say these are not the same chapter.</summary>
    Differ,

    /// <summary>Too few pages with any detail to say either way.</summary>
    Unsure,
}

/// <param name="Shares">Per part, the share of its detailed pages found in the whole.</param>
/// <param name="Cover">The share of the whole's detailed pages the parts account for.</param>
public sealed record PageMatchResult(PageMatchVerdict Verdict, IReadOnlyList<double> Shares, double Cover);

/// <summary>
/// Compares one upload of a whole chapter with one ordered set of part uploads by their pages'
/// difference hashes (<c>PerceptualHash</c>). Page counts only say two uploads are about the same size;
/// a volume's afterword, a bonus chapter or another series filed under the number add up just as well
/// (the 2026-11-17 page audit: 11 of 44 merged floors). So a fold needs the pages themselves.
/// <para>
/// Pages are aligned in reading order and each page of the whole is used once, so one blank page
/// cannot stand in for several. Near-blank pages carry too few gradients to tell chapters apart and
/// are left out. Each part must be found at <see cref="MinShare"/> or more with no long unmatched run
/// (a part that is half the chapter and half something else), and together the parts must make up
/// <see cref="MinCover"/> of the whole. Recut webtoon strips and official-against-fan scans mostly
/// come out <see cref="PageMatchVerdict.Differ"/>, which keeps both rows: the safe side.
/// </para>
/// </summary>
public static class ChapterPageMatch
{
    /// <summary>Two pages are the same page within this many differing bits.</summary>
    public const int MaxDistance = 10;

    public const double MinShare = 0.5;

    public const double MinCover = 0.5;

    /// <summary>Detailed pages each side needs before the comparison says anything.</summary>
    public const int MinPages = 3;

    /// <summary>A hash with fewer set bits than this (or more than 64 minus it) is a near-blank page.</summary>
    private const int FlatBits = 10;

    /// <summary>Longest run of a part's pages missing from the whole: this many, or a quarter of the part.</summary>
    private const int MaxGap = 4;

    private const double MaxGapShare = 0.25;

    private const int HexDigits = 16;

    public static PageMatchResult Compare(IReadOnlyList<ulong> whole, IReadOnlyList<IReadOnlyList<ulong>> parts)
    {
        var sequence = new List<(int Part, ulong Hash)>();
        var counts = new int[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            foreach (var hash in parts[i].Where(Detailed))
            {
                sequence.Add((i, hash));
                counts[i]++;
            }
        }

        var wholePages = whole.Where(Detailed).ToArray();
        if (parts.Count == 0 || counts.Min() < MinPages || wholePages.Length < MinPages)
        {
            return new PageMatchResult(PageMatchVerdict.Unsure, [], 0);
        }

        var matched = Align(sequence.Select(s => s.Hash).ToArray(), wholePages);
        var hits = new int[parts.Count];
        var gaps = new int[parts.Count];
        var run = new int[parts.Count];
        for (var i = 0; i < sequence.Count; i++)
        {
            var part = sequence[i].Part;
            if (matched[i])
            {
                hits[part]++;
                run[part] = 0;
            }
            else
            {
                run[part]++;
                gaps[part] = Math.Max(gaps[part], run[part]);
            }
        }

        var shares = hits.Select((h, i) => (double)h / counts[i]).ToList();
        var cover = (double)hits.Sum() / wholePages.Length;
        var match = cover >= MinCover && shares.All(s => s >= MinShare) &&
                    gaps.Select((g, i) => g <= Math.Max(MaxGap, MaxGapShare * counts[i])).All(ok => ok);
        return new PageMatchResult(match ? PageMatchVerdict.Match : PageMatchVerdict.Differ, shares, cover);
    }

    /// <summary>The longest in-order pairing of part pages with whole pages, each used at most once.</summary>
    private static bool[] Align(ulong[] parts, ulong[] whole)
    {
        int n = parts.Length, m = whole.Length;
        var best = new int[n + 1, m + 1];
        for (var a = n - 1; a >= 0; a--)
        {
            for (var b = m - 1; b >= 0; b--)
            {
                var value = Math.Max(best[a + 1, b], best[a, b + 1]);
                if (Same(parts[a], whole[b]))
                {
                    value = Math.Max(value, best[a + 1, b + 1] + 1);
                }

                best[a, b] = value;
            }
        }

        var matched = new bool[n];
        int i = 0, j = 0;
        while (i < n && j < m)
        {
            if (Same(parts[i], whole[j]) && best[i, j] == best[i + 1, j + 1] + 1)
            {
                matched[i] = true;
                i++;
                j++;
            }
            else if (best[i + 1, j] >= best[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return matched;
    }

    private static bool Same(ulong a, ulong b) => BitOperations.PopCount(a ^ b) <= MaxDistance;

    private static bool Detailed(ulong hash) => BitOperations.PopCount(hash) is >= FlatBits and <= 64 - FlatBits;

    /// <summary>Stored form: sixteen hex digits per page, in page order.</summary>
    public static string Format(IEnumerable<ulong> hashes)
    {
        var text = new StringBuilder();
        foreach (var hash in hashes)
        {
            text.Append(hash.ToString("x16", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public static IReadOnlyList<ulong>? Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || stored.Length % HexDigits != 0)
        {
            return null;
        }

        var hashes = new ulong[stored.Length / HexDigits];
        for (var i = 0; i < hashes.Length; i++)
        {
            if (!ulong.TryParse(stored.AsSpan(i * HexDigits, HexDigits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hashes[i]))
            {
                return null;
            }
        }

        return hashes;
    }
}
