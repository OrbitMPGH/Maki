using System.Runtime.InteropServices;

namespace Maki.Core.Images;

/// <summary>
/// A candidate's repeated region as grey rows of <see cref="CreditDetector.Width"/> columns, top to
/// bottom whichever edge it came from, so a notice looks the same at a head and a tail.
/// </summary>
public sealed record CreditRegion(byte[] Rows, int Height);

/// <summary>
/// Tells whether two credit candidates are the same notice (a credit page, a banner, a publisher card,
/// a title page), so one verdict can serve every chapter that carries it. A 16 by 16 thumbnail narrows
/// the field and the rows confirm.
/// <para>
/// Measured on 4,981 chapters (2026-11-14, git-ignored <c>research/credit-pages/2026-11-14-efficiency</c>):
/// the thumbnail alone put blank and near-blank pages from 21 series into one notice, and at a looser
/// distance a real notice took a blank page's verdict. With the rows confirmed, a region with almost
/// nothing on it never matches anything and is always read.
/// </para>
/// </summary>
public static class CreditNotices
{
    /// <summary>Cells a <see cref="Fingerprint"/> has across and down.</summary>
    public const int FingerprintSide = 16;

    /// <summary>Mean grey difference between two fingerprints above which they are not the same notice.</summary>
    public const float MaxDistance = 6f;

    /// <summary>Closest fingerprints whose rows are compared before a candidate counts as new.</summary>
    public const int Closest = 6;

    /// <summary>How far apart two partial regions' shares may be and still be the same kind.</summary>
    public const double ShareSlack = 0.03;

    private const int W = CreditDetector.Width;

    /// <summary>A row whose grey values spread wider than this (standard deviation) shows something.</summary>
    private const double InfoSpread = 8;

    /// <summary>Mean grey difference across a row below which two rows show the same thing.</summary>
    private const float RowTolerance = 14f;

    /// <summary>Share of the informative row pairs that have to match.</summary>
    private const double Need = 0.85;

    /// <summary>Informative row pairs a comparison needs before it proves anything.</summary>
    private const int MinInformative = 8;

    /// <summary>
    /// The region of <paramref name="candidate"/> at one edge of <paramref name="edges"/>: the whole page,
    /// or the top share of a head page, or the bottom share of a tail page. Null when the strip does not
    /// hold it (a page the strip cut, or one past its end).
    /// </summary>
    public static CreditRegion? RegionOf(CreditDetector.Edges edges, bool tail, CreditCandidate candidate)
    {
        var edge = tail ? edges.Tail : edges.Head;
        if (candidate.Page < 0 || candidate.Page >= edge.Pages)
        {
            return null;
        }

        var from = 0;
        for (var p = 0; p < candidate.Page; p++)
        {
            from += edge.Kept(p);
        }

        var (kept, full) = (edge.Kept(candidate.Page), edge.Full(candidate.Page));
        int height;
        if (candidate.Share >= 1)
        {
            if (kept != full)
            {
                return null;
            }

            height = full;
        }
        else
        {
            height = Math.Max(1, (int)Math.Round(full * candidate.Share));
            if (height > kept)
            {
                return null;
            }
        }

        var source = edge.Rows.Slice(from * W, height * W);
        var rows = new byte[height * W];
        if (!tail)
        {
            source.CopyTo(rows);
        }
        else
        {
            // A tail strip runs bottom up from the page's last row.
            for (var r = 0; r < height; r++)
            {
                source.Slice(r * W, W).CopyTo(rows.AsSpan((height - 1 - r) * W, W));
            }
        }

        return new CreditRegion(rows, height);
    }

    /// <summary>A mean-grey thumbnail of the region, <see cref="FingerprintSide"/> cells a side, row by row.</summary>
    public static float[] Fingerprint(CreditRegion region)
    {
        const int n = FingerprintSide;
        var (rows, height) = (region.Rows, region.Height);
        var fp = new float[n * n];
        for (var y = 0; y < n; y++)
        {
            var (r0, r1) = (y * height / n, Math.Max(y * height / n + 1, (y + 1) * height / n));
            for (var x = 0; x < n; x++)
            {
                var (c0, c1) = (x * W / n, (x + 1) * W / n);
                var sum = 0f;
                for (var r = r0; r < r1; r++)
                {
                    for (var c = c0; c < c1; c++)
                    {
                        sum += rows[r * W + c];
                    }
                }

                fp[y * n + x] = sum / ((r1 - r0) * (c1 - c0));
            }
        }

        return fp;
    }

    /// <summary>The mean absolute difference between two fingerprints.</summary>
    public static float Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += MathF.Abs(a[i] - b[i]);
        }

        return sum / a.Length;
    }

    /// <summary>Whether two candidates are the same kind of region: both whole pages, or both partial with shares within <see cref="ShareSlack"/>.</summary>
    public static bool SameKind(bool wholeA, double shareA, bool wholeB, double shareB) =>
        wholeA == wholeB && (wholeA || Math.Abs(shareA - shareB) <= ShareSlack);

    /// <summary>
    /// Whether two regions show the same thing row for row: heights within <c>max(2, 3%)</c>, and at some
    /// shift of up to two rows at least 85% of the row pairs where either row shows anything differ by a
    /// mean under 14, with at least 8 such pairs. A region with almost nothing on it matches nothing.
    /// </summary>
    public static bool SameRows(CreditRegion a, CreditRegion b)
    {
        var (ha, hb) = (a.Height, b.Height);
        if (Math.Abs(ha - hb) > Math.Max(2, (int)(0.03 * Math.Max(ha, hb))))
        {
            return false;
        }

        var infoA = Informative(a);
        var infoB = Informative(b);
        for (var shift = -2; shift <= 2; shift++)
        {
            int pairs = 0, matched = 0;
            for (var i = 0; i < ha; i++)
            {
                var j = i + shift;
                if (j < 0 || j >= hb || !(infoA[i] || infoB[j]))
                {
                    continue;
                }

                pairs++;
                var sum = 0;
                for (var x = 0; x < W; x++)
                {
                    sum += Math.Abs(a.Rows[i * W + x] - b.Rows[j * W + x]);
                }

                if (sum < RowTolerance * W)
                {
                    matched++;
                }
            }

            if (pairs >= MinInformative && matched >= Need * pairs)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A fingerprint as stored bytes.</summary>
    public static byte[] ToBytes(float[] fingerprint) => MemoryMarshal.AsBytes(fingerprint.AsSpan()).ToArray();

    /// <summary>A fingerprint <see cref="ToBytes"/> stored, or null when the bytes are not one.</summary>
    public static float[]? FromBytes(byte[] bytes) =>
        bytes.Length == FingerprintSide * FingerprintSide * sizeof(float)
            ? MemoryMarshal.Cast<byte, float>(bytes).ToArray()
            : null;

    private static bool[] Informative(CreditRegion region)
    {
        var info = new bool[region.Height];
        for (var i = 0; i < region.Height; i++)
        {
            double sum = 0, squares = 0;
            for (var x = 0; x < W; x++)
            {
                var v = region.Rows[i * W + x];
                sum += v;
                squares += v * (double)v;
            }

            var mean = sum / W;
            info[i] = Math.Sqrt(Math.Max(0, squares / W - mean * mean)) > InfoSpread;
        }

        return info;
    }
}
