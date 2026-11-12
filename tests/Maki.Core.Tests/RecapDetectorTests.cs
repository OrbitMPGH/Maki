using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>
/// Strips here are smooth value noise in panels with white gutters between them: art that never
/// repeats, so a match only happens where content was copied, and smooth enough that cutting it at
/// another row (as sites re-slice every chapter) still matches.
/// </summary>
public class RecapDetectorTests
{
    private const int W = RecapDetector.Width;

    private static byte[] Art(int seed, int rows)
    {
        var random = new Random(seed);
        var strip = new byte[rows * W];
        Array.Fill(strip, (byte)255);
        var y = 0;
        while (y < rows)
        {
            var panel = Math.Min(rows - y, random.Next(90, 220));
            const int cell = 24;
            var cols = W / 8 + 1;
            var grid = new double[(panel / cell + 2) * cols];
            for (var g = 0; g < grid.Length; g++)
            {
                grid[g] = random.Next(20, 236);
            }

            for (var r = 0; r < panel; r++)
            {
                var gy = (double)r / cell;
                var y0 = (int)gy;
                var fy = gy - y0;
                for (var x = 0; x < W; x++)
                {
                    var gx = x / 8.0;
                    var x0 = (int)gx;
                    var fx = gx - x0;
                    var top = grid[y0 * cols + x0] * (1 - fx) + grid[y0 * cols + Math.Min(x0 + 1, cols - 1)] * fx;
                    var bottom = grid[(y0 + 1) * cols + x0] * (1 - fx) + grid[(y0 + 1) * cols + Math.Min(x0 + 1, cols - 1)] * fx;
                    strip[(y + r) * W + x] = (byte)(top * (1 - fy) + bottom * fy);
                }
            }

            y += panel + random.Next(20, 60);
        }

        return strip;
    }

    private static byte[] Rows(byte[] strip, int from, int count) => strip.AsSpan(from * W, count * W).ToArray();

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static GreyPage?[] Slice(byte[] strip, int height)
    {
        var rows = strip.Length / W;
        var pages = new List<GreyPage?>();
        for (var y = 0; y < rows; y += height)
        {
            var h = Math.Min(height, rows - y);
            pages.Add(new GreyPage(Rows(strip, y, h), h));
        }

        return [.. pages];
    }

    private static int RowOf(RecapEnd end, int height) => (int)Math.Round(end.Page * height + end.Offset * height);

    [Fact]
    public void Finds_a_recap_sliced_at_another_height_and_lands_where_new_art_starts()
    {
        var previous = Art(1, 3000);
        var recap = Rows(previous, 3000 - 520, 520);
        var chapter = Join(recap, Art(2, 2000));

        var end = RecapDetector.Find(Slice(chapter, 131), Slice(previous, 126)[^20..]);

        Assert.NotNull(end);
        Assert.InRange(RowOf(end, 131), 520 - 60, 520 + 4);
    }

    [Fact]
    public void Finds_a_recap_behind_a_notice_page()
    {
        var previous = Art(3, 3000);
        var chapter = Join(Art(4, 140), Rows(previous, 3000 - 400, 400), Art(5, 1800));

        var end = RecapDetector.Find(Slice(chapter, 140), Slice(previous, 120)[^20..]);

        Assert.NotNull(end);
        Assert.InRange(RowOf(end, 140), 540 - 60, 540 + 4);
    }

    [Fact]
    public void Follows_a_condensed_recap_that_skips_part_of_the_previous_chapter()
    {
        var previous = Art(6, 3000);
        var chapter = Join(Rows(previous, 2000, 300), Rows(previous, 2700, 300), Art(7, 1800));

        var end = RecapDetector.Find(Slice(chapter, 128), Slice(previous, 125)[^20..]);

        Assert.NotNull(end);
        Assert.InRange(RowOf(end, 128), 600 - 60, 600 + 4);
    }

    [Fact]
    public void A_chapter_that_starts_with_new_art_has_no_recap()
    {
        var end = RecapDetector.Find(Slice(Art(8, 2000), 130), Slice(Art(9, 3000), 125)[^20..]);

        Assert.Null(end);
    }

    [Fact]
    public void Repeated_art_further_down_is_not_a_recap()
    {
        var previous = Art(10, 3000);
        var chapter = Join(Art(11, 1000), Rows(previous, 2600, 400), Art(12, 600));

        Assert.Null(RecapDetector.Find(Slice(chapter, 130), Slice(previous, 125)[^20..]));
    }

    [Fact]
    public void Blank_pages_match_everything_and_prove_nothing()
    {
        var white = new byte[2000 * W];
        Array.Fill(white, (byte)255);

        Assert.Null(RecapDetector.Find(Slice(white, 130), Slice(white, 125)));
    }

    [Fact]
    public void Unreadable_pages_count_as_empty()
    {
        var previous = Art(13, 3000);
        var chapter = Join(Rows(previous, 3000 - 520, 520), Art(14, 2000));
        var head = Slice(chapter, 131);
        head[10] = null;

        var end = RecapDetector.Find(head, [null, .. Slice(previous, 126)[^19..]]);

        Assert.NotNull(end);
        Assert.InRange(RowOf(end, 131), 520 - 60, 520 + 4);
    }

    [Fact]
    public void Shrink_keeps_the_aspect_at_sixty_four_columns()
    {
        using var image = (NetVips.Image.Black(640, 1280) + 90).Cast(NetVips.Enums.BandFormat.Uchar);
        using var colour = image.Bandjoin(image, image);

        var page = RecapDetector.Shrink(colour.PngsaveBuffer());

        Assert.Equal(128, page.Height);
        Assert.Equal(W * 128, page.Pixels.Length);
        Assert.All(page.Pixels, value => Assert.InRange(value, (byte)85, (byte)95));
    }
}
