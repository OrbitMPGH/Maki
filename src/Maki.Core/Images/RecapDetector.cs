using NetVips;
using VipsImage = NetVips.Image;

namespace Maki.Core.Images;

/// <summary>Where a chapter's recap of the previous chapter ends: the page new content starts on, and how far down it (0 to 1).</summary>
public record RecapEnd(int Page, double Offset);

/// <summary>One page shrunk to <see cref="RecapDetector.Width"/> grey columns, rows top to bottom.</summary>
public sealed record GreyPage(byte[] Pixels, int Height);

/// <summary>
/// Finds the recap at the top of a chapter: the last few pages of the previous chapter, repeated
/// before the new episode starts, as most webtoons do.
/// <para>
/// Pages are compared as one strip, not page by page. Sites re-slice every chapter at their own
/// height (1822 px one week, 1914 the next), so the repeated art never lands on the same page
/// boundaries twice. Each page is shrunk to 64 columns and stacked; a recap then shows as a run of
/// rows at the top of this chapter matching rows near the end of the previous one, in order.
/// </para>
/// <para>
/// The match is a chain of matching rows, built by dynamic programming over every (row here, row
/// there) pair that matches. Rows that match almost anywhere (blank gutters, black panels, smooth
/// gradients) carry no evidence and are left out. A chain may skip ahead in the previous chapter
/// (a condensed recap) and across blank rows here, but not across rows of real art that match
/// nothing, which is what new story looks like. Small trailing clusters are trimmed: a copyright
/// page of centred text can look like a title card's credits at this size.
/// </para>
/// <para>
/// Measured on 50 chapters of The Infinite Mage (2026-11-12, scripts in the git-ignored
/// <c>research/intro-skip</c>): every recap found ended where the previous chapter's art ended,
/// recaps ran 2 to 10 pages, and chapters without one never scored above 16 against a threshold of 20.
/// </para>
/// </summary>
public static class RecapDetector
{
    public const int Width = 64;

    /// <summary>Pages of the new chapter searched. The longest recap measured ran ten.</summary>
    public const int HeadPages = 15;

    /// <summary>Pages at the end of the previous chapter a recap may repeat.</summary>
    public const int TailPages = 20;

    /// <summary>A recap starts within the first pages; a credits or notice page may come first.</summary>
    private const int StartPages = 3;

    /// <summary>Mean grey difference across a row below which two rows show the same thing.</summary>
    private const float MatchTolerance = 12f;

    /// <summary>A row with less spread than this is blank or flat and proves nothing.</summary>
    private const float InfoSpread = 8f;

    /// <summary>A row matching more rows than this is generic (a gradient, a speech bubble edge).</summary>
    private const int MaxHits = 8;

    /// <summary>Rows (at half height, so about five page widths) the chain may jump forward here.</summary>
    private const int GapHere = 160;

    /// <summary>Rows the chain may jump forward in the previous chapter: a condensed recap skips some.</summary>
    private const int GapThere = 320;

    /// <summary>Rows of real art the chain may jump here without them matching anything.</summary>
    private const int SkipInfo = 10;

    /// <summary>A step back in the previous chapter this small is the re-slicing, not a jump back.</summary>
    private const int Backtrack = 4;

    private const int IslandGap = 16;
    private const int IslandMin = 10;

    /// <summary>Matching rows a recap needs. Chapters without one scored at most 16.</summary>
    private const int MinScore = 20;

    /// <summary>Shrinks a page image to <see cref="Width"/> columns of grey. Throws when it does not decode.</summary>
    public static GreyPage Shrink(byte[] image)
    {
        using var thumb = ImageCodec.Thumbnail(image, Width, 10_000_000, Enums.Size.Both);
        using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
        using var grey = srgb.Colourspace(Enums.Interpretation.Bw);
        using var flat = grey.HasAlpha() ? grey.Flatten(background: [255]) : grey.Copy();
        using var exact = flat.Width == Width ? flat.Copy() : flat.Resize((double)Width / flat.Width, vscale: 1.0);
        using var band = exact.Bands > 1 ? exact.ExtractBand(0) : exact.Copy();
        using var bytes = band.Cast(Enums.BandFormat.Uchar);
        return new GreyPage(bytes.WriteToMemory<byte>(), bytes.Height);
    }

    /// <summary>
    /// Where the recap ends in <paramref name="head"/> (the new chapter's first pages), matched
    /// against <paramref name="tail"/> (the previous chapter's last pages). Null when there is
    /// none, or when it runs to the end of what was searched. A null page could not be read and
    /// counts as empty.
    /// </summary>
    public static RecapEnd? Find(IReadOnlyList<GreyPage?> head, IReadOnlyList<GreyPage?> tail)
    {
        var a = Strip.Of(head);
        var b = Strip.Of(tail);
        if (a.Count == 0 || b.Count == 0)
        {
            return null;
        }

        var anchors = Anchors(a, b, out var okBefore);
        var startLimit = a.PageStart(Math.Min(StartPages, head.Count)) / 2;
        var end = LongestChain(anchors, okBefore, startLimit);
        if (end is not int last)
        {
            return null;
        }

        var row = last * 2 + 2;
        if (row >= a.FullRows)
        {
            return null;
        }

        var page = a.PageOf(row);
        var offset = (double)(row - a.PageStart(page)) / a.PageRows(page);
        return new RecapEnd(page, Math.Round(offset, 4));
    }

    /// <summary>Every (row here, row there) pair that matches, for rows here that say something.</summary>
    private static List<(int I, int J)> Anchors(Strip a, Strip b, out int[] okBefore)
    {
        var anchors = new List<(int, int)>();
        okBefore = new int[a.Count + 1];
        var hits = new List<int>(MaxHits + 1);
        const float limit = MatchTolerance * Width;

        for (var i = 0; i < a.Count; i++)
        {
            okBefore[i + 1] = okBefore[i];
            if (a.Spread(i) <= InfoSpread)
            {
                continue;
            }

            hits.Clear();
            var row = a.Row(i);
            for (var j = 0; j < b.Count && hits.Count <= MaxHits; j++)
            {
                var other = b.Row(j);
                var sum = 0f;
                for (var x = 0; x < Width && sum < limit; x++)
                {
                    sum += MathF.Abs(row[x] - other[x]);
                }

                if (sum < limit)
                {
                    hits.Add(j);
                }
            }

            if (hits.Count is 0 or > MaxHits)
            {
                continue;
            }

            okBefore[i + 1]++;
            foreach (var j in hits)
            {
                anchors.Add((i, j));
            }
        }

        return anchors;
    }

    /// <summary>The last row here of the best chain starting near the top, or null when it is too weak.</summary>
    private static int? LongestChain(List<(int I, int J)> anchors, int[] okBefore, int startLimit)
    {
        var score = new int[anchors.Count];
        var previous = new int[anchors.Count];
        var best = -1;

        for (var n = 0; n < anchors.Count; n++)
        {
            var (i, j) = anchors[n];
            var current = i <= startLimit ? 1 : int.MinValue / 2;
            var from = -1;
            for (var m = n - 1; m >= 0; m--)
            {
                var (pi, pj) = anchors[m];
                if (i - pi > GapHere)
                {
                    break;
                }

                if (pi >= i || pj >= j || j - pj > GapThere)
                {
                    continue;
                }

                if (j - i - (pj - pi) < -Backtrack || okBefore[i] - okBefore[pi + 1] > SkipInfo)
                {
                    continue;
                }

                if (score[m] + 1 > current)
                {
                    current = score[m] + 1;
                    from = m;
                }
            }

            score[n] = current;
            previous[n] = from;
            if (best < 0 || current > score[best] || (current == score[best] && i > anchors[best].I))
            {
                best = n;
            }
        }

        if (best < 0 || score[best] < MinScore)
        {
            return null;
        }

        var path = new List<int>();
        for (var n = best; n >= 0; n = previous[n])
        {
            path.Add(anchors[n].I);
        }

        path.Reverse();
        var islands = new List<List<int>> { new() { path[0] } };
        foreach (var i in path.Skip(1))
        {
            if (i - islands[^1][^1] > IslandGap)
            {
                islands.Add([i]);
            }
            else
            {
                islands[^1].Add(i);
            }
        }

        while (islands.Count > 1 && islands[^1].Count < IslandMin)
        {
            islands.RemoveAt(islands.Count - 1);
        }

        return islands.Sum(island => island.Count) < MinScore ? null : islands[^1][^1];
    }

    /// <summary>
    /// Pages stacked into one strip, rows averaged in pairs: each row then spans about a 30th of the
    /// page width, enough to blur away the one-row offsets re-slicing leaves.
    /// </summary>
    private sealed class Strip
    {
        private readonly float[] _rows;
        private readonly float[] _spread;
        private readonly int[] _pageOfRow;
        private readonly int[] _pageStart;

        private Strip(float[] rows, float[] spread, int[] pageOfRow, int[] pageStart, int fullRows)
        {
            _rows = rows;
            _spread = spread;
            _pageOfRow = pageOfRow;
            _pageStart = pageStart;
            FullRows = fullRows;
        }

        /// <summary>Rows after pairing.</summary>
        public int Count => _spread.Length;

        public int FullRows { get; }

        public ReadOnlySpan<float> Row(int i) => _rows.AsSpan(i * Width, Width);

        public float Spread(int i) => _spread[i];

        public int PageOf(int fullRow) => _pageOfRow[fullRow];

        public int PageStart(int page) => _pageStart[page];

        public int PageRows(int page) => _pageStart[page + 1] - _pageStart[page];

        public static Strip Of(IReadOnlyList<GreyPage?> pages)
        {
            var pageStart = new int[pages.Count + 1];
            for (var p = 0; p < pages.Count; p++)
            {
                pageStart[p + 1] = pageStart[p] + (pages[p]?.Height ?? 0);
            }

            var fullRows = pageStart[^1];
            var pageOfRow = new int[fullRows];
            var full = new byte[fullRows * Width];
            for (var p = 0; p < pages.Count; p++)
            {
                if (pages[p] is not { } page)
                {
                    continue;
                }

                Array.Fill(pageOfRow, p, pageStart[p], page.Height);
                page.Pixels.AsSpan(0, page.Height * Width).CopyTo(full.AsSpan(pageStart[p] * Width));
            }

            var count = fullRows / 2;
            var rows = new float[count * Width];
            var spread = new float[count];
            for (var i = 0; i < count; i++)
            {
                var top = (2 * i) * Width;
                var sum = 0f;
                var squares = 0f;
                for (var x = 0; x < Width; x++)
                {
                    var value = (full[top + x] + full[top + Width + x]) / 2f;
                    rows[i * Width + x] = value;
                    sum += value;
                    squares += value * value;
                }

                var mean = sum / Width;
                spread[i] = MathF.Sqrt(MathF.Max(0, squares / Width - mean * mean));
            }

            return new Strip(rows, spread, pageOfRow, pageStart, fullRows);
        }
    }
}
