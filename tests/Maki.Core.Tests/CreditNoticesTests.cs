using Maki.Core.Images;

namespace Maki.Core.Tests;

public class CreditNoticesTests
{
    private const int W = CreditDetector.Width;

    /// <summary>Seeded noise, every row different, so a row out of place is easy to spot.</summary>
    private static byte[] Noise(int seed, int rows)
    {
        var random = new Random(seed);
        var pixels = new byte[rows * W];
        random.NextBytes(pixels);
        return pixels;
    }

    private static CreditPage Page(byte[] grey) =>
        new(new GreyPage(grey, grey.Length / W), new byte[grey.Length / W], new byte[grey.Length / W]);

    private static byte[] Rows(byte[] grey, int from, int count) => grey.AsSpan(from * W, count * W).ToArray();

    private static CreditDetector.Edges Edges(byte[][] head, byte[][] tail) => new(
        CreditDetector.Edge.Of([.. head.Select(g => (CreditPage?)Page(g))], tail: false),
        CreditDetector.Edge.Of([.. tail.Select(g => (CreditPage?)Page(g))], tail: true));

    [Fact]
    public void A_whole_head_page_is_that_page()
    {
        var (first, second) = (Noise(1, 90), Noise(2, 70));
        var edges = Edges([first, second], [Noise(3, 90)]);

        var region = CreditNotices.RegionOf(edges, tail: false, new CreditCandidate(1, 1, true))!;

        Assert.Equal(70, region.Height);
        Assert.Equal(second, region.Rows);
    }

    [Fact]
    public void A_partial_head_page_is_its_top_share()
    {
        var first = Noise(1, 90);
        var edges = Edges([first], [Noise(3, 90)]);

        var region = CreditNotices.RegionOf(edges, tail: false, new CreditCandidate(0, 0.5, true))!;

        Assert.Equal(45, region.Height);
        Assert.Equal(Rows(first, 0, 45), region.Rows);
    }

    [Fact]
    public void A_whole_tail_page_reads_top_down()
    {
        var (last, before) = (Noise(4, 90), Noise(5, 60));
        // A tail's pages go edge inward: the last page first.
        var edges = Edges([Noise(1, 90)], [last, before]);

        Assert.Equal(last, CreditNotices.RegionOf(edges, tail: true, new CreditCandidate(0, 1, true))!.Rows);
        var inner = CreditNotices.RegionOf(edges, tail: true, new CreditCandidate(1, 1, true))!;
        Assert.Equal(60, inner.Height);
        Assert.Equal(before, inner.Rows);
    }

    [Fact]
    public void A_partial_tail_page_is_its_bottom_share_top_down()
    {
        var last = Noise(4, 90);
        var edges = Edges([Noise(1, 90)], [last]);

        var region = CreditNotices.RegionOf(edges, tail: true, new CreditCandidate(0, 0.4, true))!;

        Assert.Equal(36, region.Height);
        Assert.Equal(Rows(last, 54, 36), region.Rows);
    }

    [Fact]
    public void A_page_the_strip_cut_is_no_whole_region()
    {
        var edges = Edges([Noise(1, CreditDetector.EdgeRows + 20)], [Noise(3, 90)]);

        Assert.Null(CreditNotices.RegionOf(edges, tail: false, new CreditCandidate(0, 1, true)));
        Assert.Null(CreditNotices.RegionOf(edges, tail: false, new CreditCandidate(1, 1, true)));
    }

    [Fact]
    public void Fingerprints_of_one_region_are_close_and_of_different_regions_are_not()
    {
        var page = Smooth(7, 90);
        var a = CreditNotices.Fingerprint(new CreditRegion(page, 90));
        var random = new Random(3);
        var noisy = CreditNotices.Fingerprint(new CreditRegion([.. page.Select(v => (byte)Math.Clamp(v + random.Next(-4, 5), 0, 255))], 90));
        var other = CreditNotices.Fingerprint(new CreditRegion(Smooth(8, 90), 90));

        Assert.Equal(CreditNotices.FingerprintSide * CreditNotices.FingerprintSide, a.Length);
        Assert.Equal(0, CreditNotices.Distance(a, a));
        Assert.True(CreditNotices.Distance(a, noisy) <= CreditNotices.MaxDistance);
        Assert.True(CreditNotices.Distance(a, other) > CreditNotices.MaxDistance);
        Assert.Equal(a, CreditNotices.FromBytes(CreditNotices.ToBytes(a)));
    }

    [Fact]
    public void The_same_content_two_rows_off_is_the_same_rows()
    {
        var page = Noise(7, 92);

        Assert.True(CreditNotices.SameRows(new CreditRegion(Rows(page, 0, 90), 90), new CreditRegion(Rows(page, 2, 90), 90)));
    }

    [Fact]
    public void Different_content_is_not_the_same_rows()
    {
        Assert.False(CreditNotices.SameRows(new CreditRegion(Noise(7, 90), 90), new CreditRegion(Noise(8, 90), 90)));
    }

    [Fact]
    public void Two_blank_regions_are_never_the_same_rows()
    {
        var blank = Enumerable.Repeat((byte)250, 90 * W).ToArray();

        Assert.False(CreditNotices.SameRows(new CreditRegion(blank, 90), new CreditRegion((byte[])blank.Clone(), 90)));
    }

    [Fact]
    public void Regions_of_different_heights_are_not_the_same_rows()
    {
        var page = Noise(7, 120);

        Assert.False(CreditNotices.SameRows(new CreditRegion(Rows(page, 0, 90), 90), new CreditRegion(Rows(page, 0, 110), 110)));
    }

    [Fact]
    public void Whole_and_partial_regions_are_different_kinds()
    {
        Assert.True(CreditNotices.SameKind(true, 1, true, 1));
        Assert.False(CreditNotices.SameKind(true, 1, false, 0.98));
        Assert.True(CreditNotices.SameKind(false, 0.40, false, 0.42));
        Assert.False(CreditNotices.SameKind(false, 0.40, false, 0.44));
    }

    /// <summary>Smooth noise, so a thumbnail of it is not flat grey the way one of white noise is.</summary>
    private static byte[] Smooth(int seed, int rows)
    {
        var random = new Random(seed);
        var cells = new byte[(rows / 8 + 2) * (W / 8 + 2)];
        random.NextBytes(cells);
        var pixels = new byte[rows * W];
        for (var r = 0; r < rows; r++)
        {
            for (var x = 0; x < W; x++)
            {
                pixels[r * W + x] = cells[r / 8 * (W / 8 + 2) + x / 8];
            }
        }

        return pixels;
    }
}
