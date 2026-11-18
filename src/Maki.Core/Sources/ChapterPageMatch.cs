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
/// <see cref="MinCover"/> of the whole. Pages cut into slices, padded with crops or a webtoon cut at
/// other points match as strips instead (<see cref="CompareStrips"/>). Two different scans of a chapter
/// mostly come out <see cref="PageMatchVerdict.Differ"/>, which keeps both rows: the safe side.
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

    /// <summary>Two strip windows (<c>StripHash</c>) are the same picture within this many differing bits.</summary>
    public const int StripMaxDistance = 12;

    /// <summary>Detailed windows each side needs: about half a page.</summary>
    public const int MinWindows = 12;

    /// <summary>Four pages of windows: a page is about 1.43 times as tall as wide, a window every 2 rows of 32.</summary>
    private const int MaxWindowGap = 92;

    private sealed record Rules(int MaxDistance, int MinUnits, int MaxGap, bool Sparse);

    private static readonly Rules PageRules = new(MaxDistance, MinPages, MaxGap, Sparse: false);

    private static readonly Rules StripRules = new(StripMaxDistance, MinWindows, MaxWindowGap, Sparse: true);

    public static PageMatchResult Compare(IReadOnlyList<ulong> whole, IReadOnlyList<IReadOnlyList<ulong>> parts) =>
        Compare(whole, parts, PageRules);

    /// <summary>
    /// The same comparison over strips (<c>StripHash</c>): every window of a part has to be found in the
    /// whole's strip, in order. For a source whose pages are not the pages the whole is made of, cut into
    /// slices, padded with crops, or a webtoon cut at other points.
    /// </summary>
    public static PageMatchResult CompareStrips(IReadOnlyList<ulong> whole, IReadOnlyList<IReadOnlyList<ulong>> parts) =>
        Compare(whole, parts, StripRules);

    private static PageMatchResult Compare(IReadOnlyList<ulong> whole, IReadOnlyList<IReadOnlyList<ulong>> parts, Rules rules)
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
        if (parts.Count == 0 || counts.Min() < rules.MinUnits || wholePages.Length < rules.MinUnits)
        {
            return new PageMatchResult(PageMatchVerdict.Unsure, [], 0);
        }

        var hashes = sequence.Select(s => s.Hash).ToArray();
        var matched = rules.Sparse
            ? AlignSparse(hashes, wholePages, rules.MaxDistance)
            : Align(hashes, wholePages, rules.MaxDistance);
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
                    gaps.Select((g, i) => g <= Math.Max(rules.MaxGap, MaxGapShare * counts[i])).All(ok => ok);
        return new PageMatchResult(match ? PageMatchVerdict.Match : PageMatchVerdict.Differ, shares, cover);
    }

    /// <summary>The longest in-order pairing of part pages with whole pages, each used at most once.</summary>
    private static bool[] Align(ulong[] parts, ulong[] whole, int maxDistance)
    {
        int n = parts.Length, m = whole.Length;
        var best = new int[n + 1, m + 1];
        for (var a = n - 1; a >= 0; a--)
        {
            for (var b = m - 1; b >= 0; b--)
            {
                var value = Math.Max(best[a + 1, b], best[a, b + 1]);
                if (Same(parts[a], whole[b], maxDistance))
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
            if (Same(parts[i], whole[j], maxDistance) && best[i, j] == best[i + 1, j + 1] + 1)
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

    /// <summary>
    /// The same longest in-order pairing as <see cref="Align"/>, in memory for the matching pairs only
    /// (Hunt-Szymanski): a webtoon chapter's strip has thousands of windows, and a full table of both
    /// strips would not fit.
    /// </summary>
    private static bool[] AlignSparse(ulong[] parts, ulong[] whole, int maxDistance)
    {
        var ends = new List<int>();
        var tails = new List<int>();
        var nodes = new List<(int A, int Parent)>();
        var matches = new List<int>();
        for (var a = 0; a < parts.Length; a++)
        {
            matches.Clear();
            for (var b = 0; b < whole.Length; b++)
            {
                if (Same(parts[a], whole[b], maxDistance))
                {
                    matches.Add(b);
                }
            }

            for (var k = matches.Count - 1; k >= 0; k--)
            {
                var b = matches[k];
                var at = ends.BinarySearch(b);
                if (at >= 0)
                {
                    continue;
                }

                at = ~at;
                nodes.Add((a, at > 0 ? tails[at - 1] : -1));
                if (at == ends.Count)
                {
                    ends.Add(b);
                    tails.Add(nodes.Count - 1);
                }
                else
                {
                    ends[at] = b;
                    tails[at] = nodes.Count - 1;
                }
            }
        }

        var matched = new bool[parts.Length];
        for (var node = tails.Count > 0 ? tails[^1] : -1; node >= 0; node = nodes[node].Parent)
        {
            matched[nodes[node].A] = true;
        }

        return matched;
    }

    private static bool Same(ulong a, ulong b, int maxDistance) => BitOperations.PopCount(a ^ b) <= maxDistance;

    /// <summary>Stored form of a strip: its window hashes as base64, eight bytes each.</summary>
    public static string FormatStrip(IReadOnlyCollection<ulong> windows)
    {
        var bytes = new byte[windows.Count * sizeof(ulong)];
        var i = 0;
        foreach (var window in windows)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong)), window);
            i++;
        }

        return Convert.ToBase64String(bytes);
    }

    public static IReadOnlyList<ulong>? ParseStrip(string? stored)
    {
        if (stored is null)
        {
            return null;
        }

        if (stored.Length == 0)
        {
            return [];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length % sizeof(ulong) != 0)
        {
            return null;
        }

        var windows = new ulong[bytes.Length / sizeof(ulong)];
        for (var i = 0; i < windows.Length; i++)
        {
            windows[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong)));
        }

        return windows;
    }

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
