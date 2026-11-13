using NetVips;
using VipsImage = NetVips.Image;

namespace Maki.Core.Images;

/// <summary>
/// What the reader leaves out of a chapter: whole pages at its start and end, and how far down the
/// first page it shows (0 to 1) a banner fused into that page ends.
/// </summary>
public sealed record CreditPages(int HeadPages, double HeadOffset, int TailPages)
{
    public static readonly CreditPages None = new(0, 0, 0);
}

/// <summary>
/// A repeated page at one edge of a chapter that may be hidden: <paramref name="Page"/> pages from the
/// edge (0 is the first page of a head, the last of a tail), the share of it from the top that repeats
/// (1 for a whole page, less for a banner fused into the top of a slice), and whether the colour rule
/// takes it for a scanlator's.
/// </summary>
public sealed record CreditCandidate(int Page, double Share, bool Colour);

/// <summary>A chapter's <see cref="CreditCandidate"/>s at each edge, edge inward.</summary>
public sealed record CreditCandidates(
    int PageCount, IReadOnlyList<CreditCandidate> Head, IReadOnlyList<CreditCandidate> Tail)
{
    public static readonly CreditCandidates None = new(0, [], []);
}

/// <summary>
/// One page shrunk for <see cref="CreditDetector"/>: its grey rows, and for each row how many of its
/// <see cref="CreditDetector.Width"/> pixels are in colour and how many are white.
/// </summary>
public sealed record CreditPage(GreyPage Grey, byte[] Colour, byte[] White)
{
    public int Height => Grey.Height;
}

/// <summary>
/// Finds scanlator credit, recruitment, donation and notice pages at a chapter's start and end, so
/// the reader can leave them out. Publisher pages (colophons, "localized by" and "to be continued"
/// cards, title and logo pages) are kept.
/// <para>
/// Credit pages repeat: the same rows of pixels, in the same order, sit at the same edge of nearby
/// chapters. Each edge is taken as a strip of rows from the edge inward, shrunk like
/// <see cref="RecapDetector"/>'s, and a row counts as repeated when it lies on a run of matching rows
/// against at least two of the nearby chapters' same edge. Comparing head with head and tail with
/// tail is what keeps a recap of the previous chapter from ever counting. Whole-page hashes were
/// tried first and missed webtoons, where aggregators fuse their banner into a tall slice with story
/// art; rows find the banner and where it ends.
/// </para>
/// <para>
/// Measured on 1,392 chapters of 24 whole series (2026-11-12, scripts in the git-ignored
/// <c>research/credit-pages</c>), before the colour test below: 490 chapters got at least one whole page hidden and 190 a banner cut
/// at the top of a slice. 48 hidden pages and 24 cut lines checked by eye held no story art; about
/// one in six hidden pages was a title, logo or repeated cover page.
/// </para>
/// <para>
/// Only scanlator pages are hidden. The repeated pages are candidates (<see cref="Candidates"/>):
/// the Api reads the text on each and asks <see cref="CreditText"/>, then <see cref="Settle"/>
/// hides the run of accepted pages from each edge. Without OCR the colour rule judges alone
/// (<see cref="Find"/>). Publisher legal pages are black and white and scanlator pages almost
/// always in colour; in a colour chapter, where everything is in colour, a publisher's title card
/// is a logo on a white ground. So a repeated page passes when it is in colour, and in a colour
/// chapter only when it is not mostly white. On the hand-labelled pages (2026-11-12) colour kept
/// 101 of 116 scanlator pages hidden, 0 of 53 publisher pages and 9 of 44 title or cover pages;
/// the text rule 115, 0 and 1. Colour misses black and white scanlator pages, and scan banners on a
/// white ground in colour chapters, and still hides repeated colour covers.
/// </para>
/// </summary>
public static class CreditDetector
{
    public const int Width = RecapDetector.Width;

    /// <summary>Pages an edge strip may take from the chapter's edge inward.</summary>
    public const int EdgePages = 4;

    /// <summary>Rows an edge strip collects before pairing. A page taller than what is left is cut.</summary>
    public const int EdgeRows = 384;

    /// <summary>Nearby chapters on each side compared against, in reading order.</summary>
    public const int References = 3;

    /// <summary>A reference shorter than this is not compared: its edges are the whole chapter.</summary>
    public const int MinReferencePages = 4;

    /// <summary>A chapter shorter than this hides nothing.</summary>
    public const int MinPages = 6;

    /// <summary>Mean grey difference across a paired row below which two rows show the same thing.</summary>
    private const float MatchTolerance = 12f;

    /// <summary>A paired row with this little spread or less is blank or flat and proves nothing.</summary>
    private const float InfoSpread = 8f;

    /// <summary>Informative matching rows a run needs before it counts.</summary>
    private const int MinRun = 6;

    /// <summary>References a row has to be repeated in.</summary>
    private const int Support = 2;

    /// <summary>Informative rows in a row that may match nothing before the region ends.</summary>
    private const int MaxGap = 4;

    /// <summary>Paired rows that may be hidden, about five page widths.</summary>
    private const int MaxRows = 160;

    /// <summary>A run starting this close to the edge on a diagonal this close to the edge's is the edge itself.</summary>
    private const int EdgeSlack = 2;

    /// <summary>A reference whose run from the edge reaches this close to the shorter strip's end is the same chapter again.</summary>
    private const int DuplicateSlack = 4;

    /// <summary>
    /// A reference covering this share of the informative rows in the strip's innermost quarter is the
    /// same chapter again, even when a dark or damaged row breaks the run from the edge. Credits end
    /// before that quarter; a copy of the chapter does not.
    /// </summary>
    private const double DuplicateShare = 0.9;

    /// <summary>A region reaching this close to the strip's end did not end at all: the strip ran out.</summary>
    private const int EndSlack = 2;

    /// <summary>Share of a page's rows the region has to cover for the page to be hidden whole.</summary>
    private const double WholeShare = 0.9;

    /// <summary>Whole pages hidden at either edge at most.</summary>
    private const int MaxPagesPerEdge = 3;

    /// <summary>A banner ending this close to the top of the first page shown is not worth moving for.</summary>
    private const double MinOffset = 0.03;

    /// <summary>A pixel whose channels spread further apart than this is in colour.</summary>
    private const int ColourSpread = 40;

    /// <summary>A pixel whose every channel is above this is white.</summary>
    private const int WhiteFloor = 235;

    /// <summary>
    /// Rows with less colour than this share of their pixels are black and white: beyond the repeated
    /// region they make the chapter grey, and a repeated page this grey is a publisher's.
    /// </summary>
    private const double ColourShare = 0.02;

    /// <summary>Rows beyond the repeated region an edge needs before its colour says anything about the chapter.</summary>
    private const int MinChapterRows = 64;

    /// <summary>In a colour chapter, a repeated page at least this white is a logo on a white ground: a title card.</summary>
    private const double MaxWhiteShare = 0.5;

    /// <summary>Both edges of a chapter shrunk into strips, the unit cached and compared.</summary>
    public sealed record Edges(Edge Head, Edge Tail)
    {
        public int Bytes => Head.Bytes + Tail.Bytes;
    }

    /// <summary>
    /// One edge of a chapter: rows from the edge inward, at most <see cref="EdgeRows"/> of them over at
    /// most <see cref="EdgePages"/> pages. A tail's rows run bottom up, so index 0 is always the edge.
    /// </summary>
    public sealed class Edge
    {
        private readonly byte[] _rows;
        private readonly byte[] _colour;
        private readonly byte[] _white;
        private readonly int[] _kept;
        private readonly int[] _full;

        private Edge(byte[] rows, byte[] colour, byte[] white, int[] kept, int[] full)
        {
            _rows = rows;
            _colour = colour;
            _white = white;
            _kept = kept;
            _full = full;
        }

        public int Bytes => _rows.Length + _colour.Length + _white.Length;

        internal int Pages => _kept.Length;

        internal int RowCount => _colour.Length;

        internal int Kept(int page) => _kept[page];

        internal int Full(int page) => _full[page];

        internal ReadOnlySpan<byte> Rows => _rows;

        /// <summary>The share of the pixels in <paramref name="count"/> rows from <paramref name="from"/> that are in colour, and that are white.</summary>
        internal (double Colour, double White) Shares(int from, int count)
        {
            if (count <= 0)
            {
                return (0, 0);
            }

            var (colour, white) = (0, 0);
            for (var r = from; r < from + count; r++)
            {
                colour += _colour[r];
                white += _white[r];
            }

            return ((double)colour / (count * Width), (double)white / (count * Width));
        }

        /// <summary>
        /// The strip from <paramref name="pages"/>, ordered from the edge inward: a head's first page
        /// first, a tail's last page first. The strip stops at a page that could not be read (null), so
        /// what is hidden is always a run of pages from the edge.
        /// </summary>
        public static Edge Of(IReadOnlyList<CreditPage?> pages, bool tail)
        {
            var kept = new List<int>();
            var full = new List<int>();
            var rows = new List<byte>();
            var colour = new List<byte>();
            var white = new List<byte>();
            for (var p = 0; p < Math.Min(EdgePages, pages.Count) && colour.Count < EdgeRows; p++)
            {
                if (pages[p] is not { } page || page.Height <= 0)
                {
                    break;
                }

                var keep = Math.Min(page.Height, EdgeRows - colour.Count);
                for (var r = 0; r < keep; r++)
                {
                    var source = tail ? page.Height - 1 - r : r;
                    rows.AddRange(page.Grey.Pixels.AsSpan(source * Width, Width));
                    colour.Add(page.Colour[source]);
                    white.Add(page.White[source]);
                }

                kept.Add(keep);
                full.Add(page.Height);
            }

            return new Edge([.. rows], [.. colour], [.. white], [.. kept], [.. full]);
        }
    }

    /// <summary>
    /// Shrinks a page image for <see cref="Edge.Of"/> in one decode: <see cref="Width"/> columns of grey,
    /// as <see cref="RecapDetector.Shrink"/> makes them, and each row's colour and white pixels counted
    /// on the sRGB thumbnail with any alpha flattened onto white. Throws when libvips cannot read it.
    /// </summary>
    public static CreditPage Shrink(byte[] image)
    {
        using var thumb = VipsImage.ThumbnailBuffer(image, Width, height: 10_000_000, size: Enums.Size.Both);
        using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
        using var flat = srgb.HasAlpha() ? srgb.Flatten(background: [255, 255, 255]) : srgb.Copy();
        using var exact = flat.Width == Width ? flat.Copy() : flat.Resize((double)Width / flat.Width, vscale: 1.0);
        using var cast = exact.Cast(Enums.BandFormat.Uchar);
        // Read twice below (grey and colour), and a thumbnail can only be read through once.
        using var rgb = cast.CopyMemory();
        using var grey = rgb.Colourspace(Enums.Interpretation.Bw);
        using var band = grey.Bands > 1 ? grey.ExtractBand(0) : grey.Copy();
        using var greyBytes = band.Cast(Enums.BandFormat.Uchar);

        var height = rgb.Height;
        var bands = rgb.Bands;
        var pixels = rgb.WriteToMemory<byte>();
        var colour = new byte[height];
        var white = new byte[height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var at = (y * Width + x) * bands;
                int max = pixels[at], min = pixels[at];
                for (var c = 1; c < Math.Min(bands, 3); c++)
                {
                    max = Math.Max(max, pixels[at + c]);
                    min = Math.Min(min, pixels[at + c]);
                }

                colour[y] += (byte)(max - min > ColourSpread ? 1 : 0);
                white[y] += (byte)(min > WhiteFloor ? 1 : 0);
            }
        }

        return new CreditPage(new GreyPage(greyBytes.WriteToMemory<byte>(), greyBytes.Height), colour, white);
    }

    /// <summary>
    /// What to hide in a chapter of <paramref name="pageCount"/> pages, from its edges compared with
    /// those of the chapters near it (up to <see cref="References"/> each side, in any order), judging
    /// each repeated page by colour alone.
    /// </summary>
    public static CreditPages Find(int pageCount, Edges chapter, IReadOnlyList<Edges> references)
    {
        var candidates = Candidates(pageCount, chapter, references);
        return Settle(candidates, Leading(candidates.Head), Leading(candidates.Tail));
    }

    /// <summary>How many candidates from the edge the colour rule accepts before the first it does not.</summary>
    private static int Leading(IReadOnlyList<CreditCandidate> edge)
    {
        var count = 0;
        while (count < edge.Count && edge[count].Colour)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The repeated pages at each edge of the chapter, edge inward, each with the colour rule's
    /// verdict, for a caller that judges them some other way (<see cref="Settle"/> takes the result).
    /// Pages past what the caps could ever hide are left out, so nothing judges them for nothing.
    /// </summary>
    public static CreditCandidates Candidates(int pageCount, Edges chapter, IReadOnlyList<Edges> references)
    {
        if (pageCount < MinPages || references.Count < Support)
        {
            return CreditCandidates.None;
        }

        var head = Covered(chapter.Head, references.Select(r => r.Head));
        var tail = Covered(chapter.Tail, references.Select(r => r.Tail));
        var grey = IsGrey((chapter.Head, head), (chapter.Tail, tail));
        var limit = Math.Min(MaxPagesPerEdge, pageCount / 3);
        return new CreditCandidates(
            pageCount,
            EdgeCandidates(chapter.Head, head, grey, limit, banner: true),
            EdgeCandidates(chapter.Tail, tail, grey, limit, banner: false));
    }

    /// <summary>
    /// What to hide once <paramref name="headAccepted"/> candidates from the head and
    /// <paramref name="tailAccepted"/> from the tail are known to be a scanlator's: the run from each
    /// edge stops at the first that is not, and the caps apply after.
    /// </summary>
    public static CreditPages Settle(CreditCandidates candidates, int headAccepted, int tailAccepted)
    {
        var (headPages, headShare) = Accepted(candidates.Head, headAccepted);
        var (tailPages, _) = Accepted(candidates.Tail, tailAccepted);

        if (headPages > MaxPagesPerEdge)
        {
            headPages = MaxPagesPerEdge;
            headShare = 0;
        }

        var allowed = candidates.PageCount / 3;
        if (headPages > allowed)
        {
            headPages = allowed;
            headShare = 0;
        }

        tailPages = Math.Max(0, Math.Min(Math.Min(tailPages, MaxPagesPerEdge), allowed - headPages));
        if (headShare < MinOffset)
        {
            headShare = 0;
        }

        return new CreditPages(headPages, Math.Round(headShare, 4), tailPages);
    }

    private static (int Pages, double Share) Accepted(IReadOnlyList<CreditCandidate> edge, int accepted)
    {
        var pages = 0;
        var share = 0.0;
        for (var c = 0; c < Math.Min(accepted, edge.Count); c++)
        {
            if (edge[c].Share >= 1)
            {
                pages++;
            }
            else
            {
                share = edge[c].Share;
            }
        }

        return (pages, share);
    }

    /// <summary>Rows of the edge, before pairing, that are repeated at the same edge of the references; 0 for none.</summary>
    private static int Covered(Edge edge, IEnumerable<Edge> references) =>
        Region(Strip.Of(edge, 0, 0), references.Select(Placements)) * 2;

    /// <summary>
    /// A reference's strip at each <see cref="Down"/> and <see cref="Across"/> step, the unshifted one
    /// first. Aggregators paste the same notice a few pixels off from one chapter to the next
    /// (Thunderscans' on Shadow Slave moves 2 px across and 18 px down), and after shrinking that puts
    /// thin text lines part way between two paired rows or columns, where they match on no diagonal.
    /// Only the reference is shifted: the chapter's own rows stay sharp, so unrelated rows of thin
    /// line art come no closer.
    /// </summary>
    private static List<Strip> Placements(Edge reference) =>
        [.. Enumerable.Range(0, Down).SelectMany(down => Across.Select(across => Strip.Of(reference, down, across)))];

    /// <summary>Steps of half a source row a reference is moved down by, so a quarter of a paired row at most is left over.</summary>
    private const int Down = 4;

    /// <summary>Shares of a column a reference is moved across by.</summary>
    private static readonly float[] Across = [0, -0.25f, 0.25f, -0.5f, 0.5f];

    /// <summary>
    /// Whether the chapter is black and white, judged on the rows beyond each edge's repeated region:
    /// the lower colour share of the edges with enough of them. Neither edge having enough counts as
    /// colour, the case that hides less.
    /// </summary>
    private static bool IsGrey(params (Edge Edge, int Covered)[] edges)
    {
        double? lowest = null;
        foreach (var (edge, covered) in edges)
        {
            var left = edge.RowCount - covered;
            if (left < MinChapterRows)
            {
                continue;
            }

            var share = edge.Shares(covered, left).Colour;
            lowest = lowest is { } low ? Math.Min(low, share) : share;
        }

        return lowest < ColourShare;
    }

    /// <summary>Whether rows of a repeated page look like a scanlator's: in colour, and in a colour chapter not mostly white.</summary>
    private static bool Scanlator(Edge edge, int from, int count, bool grey)
    {
        var (colour, white) = edge.Shares(from, count);
        return colour > ColourShare && (grey || white < MaxWhiteShare);
    }

    /// <summary>
    /// The whole pages inside the repeated region at this edge, then the page the region ends part way
    /// down (a head's only, and only when the region covers enough of it to move for), at most
    /// <paramref name="limit"/> whole pages. A page cut by the row budget was never seen whole, so it is
    /// never a whole candidate.
    /// </summary>
    private static List<CreditCandidate> EdgeCandidates(Edge edge, int covered, bool grey, int limit, bool banner)
    {
        var found = new List<CreditCandidate>();
        if (covered == 0)
        {
            return found;
        }

        var start = 0;
        for (var p = 0; p < edge.Pages; p++)
        {
            var count = Math.Clamp(covered - start, 0, edge.Kept(p));
            if (count == 0)
            {
                return found;
            }

            var whole = edge.Kept(p) == edge.Full(p) && count >= WholeShare * edge.Full(p);
            if (!whole)
            {
                var share = (double)count / edge.Full(p);
                if (banner && share >= MinOffset)
                {
                    found.Add(new CreditCandidate(p, share, Scanlator(edge, start, count, grey)));
                }

                return found;
            }

            if (found.Count == limit)
            {
                // A further whole page would be capped away, and so would the banner behind it.
                return found;
            }

            found.Add(new CreditCandidate(p, 1, Scanlator(edge, start, edge.Kept(p), grey)));
            start += edge.Kept(p);
        }

        return found;
    }

    /// <summary>
    /// Paired rows from the edge that are repeated at the same edge of other chapters, or 0 for none.
    /// A row is repeated in a reference when it lies on a run against any of its placements. The
    /// region starts where <see cref="Support"/> references repeat it, and runs on as far as any one
    /// of them still does: a notice pasted a pixel off in each chapter matches its neighbours to
    /// different depths, and the closest one shows where it ends.
    /// </summary>
    private static int Region(Strip a, IEnumerable<List<Strip>> references)
    {
        var support = new int[a.Count];
        var repeats = new List<bool[]>();
        foreach (var placements in references)
        {
            var covered = new bool[a.Count];
            var duplicate = false;
            foreach (var b in placements)
            {
                var (cover, same) = Runs(a, b);
                if (same)
                {
                    duplicate = true;
                    break;
                }

                for (var i = 0; i < a.Count; i++)
                {
                    covered[i] |= cover[i];
                }
            }

            if (duplicate)
            {
                continue;
            }

            repeats.Add(covered);
            for (var i = 0; i < a.Count; i++)
            {
                if (covered[i])
                {
                    support[i]++;
                }
            }
        }

        var end = Reach(a, 0, i => support[i] >= Support);
        if (end > 0)
        {
            end = repeats.Max(covered => Reach(a, end, i => covered[i]));
        }

        return end == 0 || end >= a.Count - EndSlack ? 0 : end;
    }

    /// <summary>Where a run of <paramref name="repeated"/> rows from <paramref name="from"/> ends, allowing <see cref="MaxGap"/> informative rows that are not.</summary>
    private static int Reach(Strip a, int from, Func<int, bool> repeated)
    {
        var end = from;
        var gap = 0;
        for (var i = from; i < Math.Min(a.Count, MaxRows); i++)
        {
            if (!a.Informative(i))
            {
                continue;
            }

            if (repeated(i))
            {
                end = i + 1;
                gap = 0;
            }
            else if (++gap > MaxGap)
            {
                break;
            }
        }

        return end;
    }

    /// <summary>
    /// Which rows of <paramref name="a"/> lie on a run of matching rows against <paramref name="b"/>,
    /// along one diagonal of (row here, row there), and whether <paramref name="b"/> is the same
    /// chapter again. Informative matches count toward the run; a flat row against a flat row passes
    /// through without counting; anything else ends it.
    /// </summary>
    private static (bool[] Cover, bool Duplicate) Runs(Strip a, Strip b)
    {
        var cover = new bool[a.Count];
        if (a.Count == 0 || b.Count == 0)
        {
            return (cover, false);
        }

        var fromEdge = 0;
        for (var offset = -(a.Count - 1); offset < b.Count; offset++)
        {
            var start = -1;
            var score = 0;
            for (int i = Math.Max(0, -offset), j = i + offset; i <= a.Count && j <= b.Count; i++, j++)
            {
                var matched = false;
                var counts = false;
                if (i < a.Count && j < b.Count && a.Informative(i) == b.Informative(j) && Close(a.Row(i), b.Row(j)))
                {
                    matched = true;
                    counts = a.Informative(i);
                }

                if (matched)
                {
                    if (start < 0)
                    {
                        start = i;
                        score = 0;
                    }

                    if (counts)
                    {
                        score++;
                    }

                    continue;
                }

                if (start >= 0 && score >= MinRun)
                {
                    Array.Fill(cover, true, start, i - start);
                    if (start <= EdgeSlack && Math.Abs(offset) <= EdgeSlack)
                    {
                        fromEdge = Math.Max(fromEdge, i - start);
                    }
                }

                start = -1;
            }
        }

        var informative = 0;
        var covered = 0;
        for (var i = a.Count * 3 / 4; i < a.Count; i++)
        {
            if (a.Informative(i))
            {
                informative++;
                covered += cover[i] ? 1 : 0;
            }
        }

        var duplicate = fromEdge >= Math.Min(a.Count, b.Count) - DuplicateSlack
            || (informative >= MinRun && covered >= DuplicateShare * informative);
        return (cover, duplicate);
    }

    private static bool Close(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        const float limit = MatchTolerance * Width;
        var sum = 0f;
        for (var x = 0; x < Width && sum < limit; x++)
        {
            sum += MathF.Abs(a[x] - b[x]);
        }

        return sum < limit;
    }

    /// <summary>An edge's rows averaged in pairs, with which of them say something.</summary>
    private sealed class Strip
    {
        private readonly float[] _rows;
        private readonly bool[] _informative;

        private Strip(float[] rows, bool[] informative)
        {
            _rows = rows;
            _informative = informative;
        }

        public int Count => _informative.Length;

        public ReadOnlySpan<float> Row(int i) => _rows.AsSpan(i * Width, Width);

        public bool Informative(int i) => _informative[i];

        /// <summary>
        /// The edge's rows paired from <paramref name="down"/> half rows in, each moved
        /// <paramref name="across"/> of a column to the right (both interpolated).
        /// </summary>
        public static Strip Of(Edge edge, int down, float across)
        {
            var full = edge.Rows;
            var (skip, half) = (down / 2, down % 2 == 1);
            var count = Math.Max(0, full.Length / Width - skip - (half ? 1 : 0)) / 2;
            var rows = new float[count * Width];
            var informative = new bool[count];
            var paired = new float[Width];
            var (step, keep) = (across < 0 ? 1 : -1, 1 - MathF.Abs(across));
            for (var i = 0; i < count; i++)
            {
                var top = (2 * i + skip) * Width;
                for (var x = 0; x < Width; x++)
                {
                    paired[x] = half
                        ? (full[top + x] / 2f + full[top + Width + x] + full[top + 2 * Width + x] / 2f) / 2f
                        : (full[top + x] + full[top + Width + x]) / 2f;
                }

                var sum = 0f;
                var squares = 0f;
                for (var x = 0; x < Width; x++)
                {
                    var value = paired[x] * keep + paired[Math.Clamp(x + step, 0, Width - 1)] * (1 - keep);
                    rows[i * Width + x] = value;
                    sum += value;
                    squares += value * value;
                }

                var mean = sum / Width;
                informative[i] = MathF.Sqrt(MathF.Max(0, squares / Width - mean * mean)) > InfoSpread;
            }

            return new Strip(rows, informative);
        }
    }
}
