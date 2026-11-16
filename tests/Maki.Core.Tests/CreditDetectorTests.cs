using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>
/// Story pages are seeded value noise in panels with white gutters, so they never repeat by chance.
/// Credit pages are seeded noise with no gutters, the same pixels wherever they are shared, the way a
/// scanlator's page sits unchanged at the start of every chapter they release. A page is in colour or
/// black and white as a whole: in colour, every pixel that is not white counts as coloured.
/// </summary>
public class CreditDetectorTests
{
    private const int W = CreditDetector.Width;
    private const int PageRows = 90;

    private static byte[] Art(int seed, int rows, bool gutters = true)
    {
        var random = new Random(seed);
        var strip = new byte[rows * W];
        Array.Fill(strip, (byte)255);
        var y = 0;
        while (y < rows)
        {
            var panel = gutters ? Math.Min(rows - y, random.Next(90, 220)) : rows - y;
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

            y += panel + (gutters ? random.Next(20, 60) : 0);
        }

        return strip;
    }

    private static CreditPage Page(byte[] grey, bool colour)
    {
        var height = grey.Length / W;
        var (coloured, white) = (new byte[height], new byte[height]);
        for (var i = 0; i < grey.Length; i++)
        {
            if (grey[i] > 235)
            {
                white[i / W]++;
            }
            else if (colour)
            {
                coloured[i / W]++;
            }
        }

        return new CreditPage(new GreyPage(grey, height), coloured, white);
    }

    private static CreditPage Story(int seed, int rows = PageRows, bool colour = false) => Page(Art(seed, rows), colour);

    private static CreditPage Credit(int seed, int rows = PageRows, bool colour = true) => Page(Art(seed, rows, gutters: false), colour);

    /// <summary>A publisher's colophon: repeated like a credit page, but black and white.</summary>
    private static CreditPage Colophon(int seed) => Credit(seed, colour: false);

    /// <summary>A colour logo down the left of the page, on a ground of <paramref name="ground"/> across the rest.</summary>
    private static CreditPage Logo(int seed, byte ground)
    {
        var art = Art(seed, PageRows, gutters: false);
        for (var i = 0; i < art.Length; i++)
        {
            if (i % W >= 24)
            {
                art[i] = ground;
            }
        }

        return Page(art, colour: true);
    }

    private static CreditPage Join(params CreditPage[] parts) => new(
        new GreyPage([.. parts.SelectMany(p => p.Grey.Pixels)], parts.Sum(p => p.Height)),
        [.. parts.SelectMany(p => p.Colour)],
        [.. parts.SelectMany(p => p.White)]);

    private static CreditPage Rows(CreditPage page, int from, int count) => new(
        new GreyPage(page.Grey.Pixels.AsSpan(from * W, count * W).ToArray(), count),
        page.Colour[from..(from + count)],
        page.White[from..(from + count)]);

    /// <summary>A chapter's edges, from its pages in reading order.</summary>
    private static CreditDetector.Edges Chapter(params CreditPage[] pages) =>
        new(CreditDetector.Edge.Of(pages, tail: false), CreditDetector.Edge.Of(pages.Reverse().ToArray(), tail: true));

    /// <summary>A chapter of story pages from <paramref name="seed"/>, with the given pages before and after them.</summary>
    private static CreditPage[] Pages(int seed, CreditPage[] head, CreditPage[] tail, int story = 10, bool colour = false) =>
        [.. head, .. Enumerable.Range(0, story).Select(n => Story(seed * 100 + n, colour: colour)), .. tail];

    private static CreditPages Find(CreditPage[] chapter, params CreditPage[][] references) =>
        CreditDetector.Find(chapter.Length, Chapter(chapter), [.. references.Select(Chapter)]);

    [Fact]
    public void Stored_strips_read_back_to_the_same_answer()
    {
        var credit = Credit(7);
        var chapters = new[] { Pages(1, [credit], [credit]), Pages(2, [credit], [credit]), Pages(3, [credit], [credit]) };

        CreditDetector.Edges RoundTrip(CreditDetector.Edges edges)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                edges.Write(writer);
            }

            stream.Position = 0;
            using var reader = new BinaryReader(stream);
            return CreditDetector.Edges.Read(reader);
        }

        var stored = chapters.Select(c => RoundTrip(Chapter(c))).ToList();

        Assert.Equal(
            CreditDetector.Find(chapters[0].Length, Chapter(chapters[0]), [.. chapters.Skip(1).Select(Chapter)]),
            CreditDetector.Find(chapters[0].Length, stored[0], stored[1..]));
        Assert.Equal(new CreditPages(1, 0, 1), CreditDetector.Find(chapters[0].Length, stored[0], stored[1..]));
    }

    [Fact]
    public void A_credit_page_seen_in_another_chapter_is_a_bank_candidate()
    {
        var credit = Credit(7);
        var source = Chapter(Pages(1, [credit], []));
        var lone = Pages(2, [credit], []);

        var found = CreditDetector.BankCandidates(lone.Length, Chapter(lone), source.Head, tail: false);

        Assert.Equal(0, Assert.Single(found).Page);
        Assert.Empty(CreditDetector.BankCandidates(lone.Length, Chapter(Pages(3, [], [])), source.Head, tail: false));
    }

    [Fact]
    public void Signatures_of_one_page_are_alike_and_of_different_pages_are_not()
    {
        var credit = CreditDetector.SignatureOf(Chapter(Pages(1, [Credit(7)], [])).Head);
        var again = CreditDetector.SignatureOf(Chapter(Pages(2, [Credit(7)], [])).Head);
        var other = CreditDetector.SignatureOf(Chapter(Pages(3, [Credit(8)], [])).Head);

        Assert.NotNull(credit);
        Assert.True(CreditDetector.Alike(credit, again!));
        Assert.False(CreditDetector.Alike(credit, other!));
    }

    [Fact]
    public void A_blank_edge_has_no_signature()
    {
        var blank = new CreditPage(new GreyPage(Enumerable.Repeat((byte)255, W * 40).ToArray(), 40), new byte[40], new byte[40]);

        Assert.Null(CreditDetector.SignatureOf(CreditDetector.Edge.Of([blank], tail: false)));
    }

    [Fact]
    public void A_stream_that_holds_no_strip_is_refused()
    {
        using var reader = new BinaryReader(new MemoryStream([9, 0, 0, 0]));

        Assert.Throws<InvalidDataException>(() => CreditDetector.Edge.Read(reader));
    }

    [Fact]
    public void A_credit_page_shared_by_three_chapters_is_hidden_whole()
    {
        var credit = Credit(7);

        var found = Find(Pages(1, [credit], []), Pages(2, [credit], []), Pages(3, [credit], []), Pages(4, [], []));

        Assert.Equal(new CreditPages(1, 0, 0), found);
    }

    [Fact]
    public void A_page_shared_with_only_one_reference_is_kept()
    {
        var credit = Credit(7);

        var found = Find(Pages(1, [credit], []), Pages(2, [credit], []), Pages(3, [], []), Pages(4, [], []));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void Tail_pages_are_found_against_the_tails_of_nearby_chapters()
    {
        var recruit = Credit(8);
        var donate = Credit(9);

        var found = Find(
            Pages(1, [], [recruit, donate]),
            Pages(2, [], [recruit, donate]),
            Pages(3, [], [recruit, donate]),
            Pages(4, [], [donate]));

        Assert.Equal(new CreditPages(0, 0, 2), found);
    }

    [Fact]
    public void A_banner_fused_into_the_top_of_a_tall_slice_gives_where_it_ends()
    {
        var banner = Credit(5, 100);
        CreditPage[] Webtoon(int seed) =>
            [Join(banner, Story(seed, 500)), .. Enumerable.Range(1, 8).Select(n => Story(seed * 100 + n, 600))];

        var found = Find(Webtoon(1), Webtoon(2), Webtoon(3), Webtoon(4));

        Assert.Equal(0, found.HeadPages);
        Assert.InRange(found.HeadOffset, 100 / 600.0 - 0.02, 100 / 600.0 + 0.005);
        Assert.Equal(0, found.TailPages);
    }

    /// <summary>
    /// A notice of thin bright text lines on a dark ground, drawn <paramref name="down"/> rows lower and
    /// <paramref name="across"/> of a column further right, the way an aggregator pastes the same notice
    /// a few pixels off from one chapter to the next.
    /// </summary>
    private static CreditPage Notice(int rows, int down, double across)
    {
        var random = new Random(11);
        var sharp = new byte[(rows + 2) * W];
        Array.Fill(sharp, (byte)20);
        for (var line = 4; line + 2 < rows; line += 7)
        {
            for (var x = 4; x < W - 4;)
            {
                var (lit, run) = (random.Next(2) == 0 ? (byte)230 : (byte)20, random.Next(2, 5));
                for (var end = Math.Min(W - 4, x + run); x < end; x++)
                {
                    sharp[line * W + x] = lit;
                    sharp[(line + 1) * W + x] = lit;
                }
            }
        }

        var grey = new byte[rows * W];
        for (var r = 0; r < rows; r++)
        {
            var source = Math.Clamp(r - down, 0, rows + 1) * W;
            for (var x = 0; x < W; x++)
            {
                grey[r * W + x] = (byte)Math.Round(sharp[source + x] * (1 - across) + sharp[source + Math.Max(0, x - 1)] * across);
            }
        }

        return Page(grey, colour: true);
    }

    [Fact]
    public void A_notice_pasted_a_few_pixels_off_in_each_chapter_is_still_found()
    {
        CreditPage[] Webtoon(int seed, int down, double across) =>
            [Join(Notice(100, down, across), Story(seed, 500, colour: true)), .. Enumerable.Range(1, 8).Select(n => Story(seed * 100 + n, 600, colour: true))];

        var found = Find(Webtoon(1, 0, 0), Webtoon(2, 1, 0), Webtoon(3, 0, 0.2), Webtoon(4, 1, 0.2));

        Assert.Equal(0, found.HeadPages);
        Assert.InRange(found.HeadOffset, 100 / 600.0 - 0.03, 100 / 600.0 + 0.005);
    }

    [Fact]
    public void The_same_chapter_in_the_library_again_proves_nothing()
    {
        var credit = Credit(7);
        var chapter = Pages(1, [credit], [credit]);

        var found = Find(chapter, chapter, chapter, chapter);

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void A_copy_of_the_chapter_with_a_damaged_band_still_proves_nothing()
    {
        var story = Pages(1, [], []);
        CreditPage[] Copy(int seed) =>
            [Join(Rows(story[0], 0, 30), Credit(seed, 6), Rows(story[0], 36, PageRows - 36)), .. story.Skip(1)];

        var found = Find(Copy(21), Copy(22), Copy(23), Copy(24));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void A_page_sharing_only_its_top_and_bottom_rows_is_not_hidden_whole()
    {
        var notes = Credit(6);
        CreditPage Variant(int seed) => Join(Rows(notes, 0, 20), Credit(seed, 50), Rows(notes, 70, 20));

        var found = Find(
            Pages(1, [Variant(11)], []),
            Pages(2, [Variant(12)], []),
            Pages(3, [Variant(13)], []),
            Pages(4, [Variant(14)], []));

        Assert.Equal(0, found.HeadPages);
        Assert.InRange(found.HeadOffset, 0.15, 0.25);
    }

    [Fact]
    public void A_recap_of_the_previous_chapters_ending_does_not_count()
    {
        var ending = Story(42);

        // The chapter opens with a page two others end on: a recap, not a credit.
        var found = Find(
            Pages(1, [ending], []),
            Pages(2, [], [ending]),
            Pages(3, [], [ending]),
            Pages(4, [], []));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void At_most_three_whole_pages_are_hidden_at_an_edge()
    {
        // The fourth page is shared but for its last rows, so it counts as whole while the chapters
        // still differ before the strip ends. It is mostly blank, which keeps the shared rows under
        // the share that would make the references copies of the chapter.
        var blank = Page(Enumerable.Repeat((byte)255, 162 * W).ToArray(), colour: false);
        CreditPage[] head = [Credit(21, 40), Credit(22, 40), Credit(23, 40)];
        CreditPage[] Varied(int seed) => [.. head, Join(blank, Credit(24, 20), Credit(seed, 18))];

        var found = Find(Pages(1, Varied(31), []), Pages(2, Varied(32), []), Pages(3, Varied(33), []));

        Assert.Equal(new CreditPages(3, 0, 0), found);
    }

    [Fact]
    public void Hidden_pages_never_take_more_than_a_third_of_the_chapter()
    {
        CreditPage[] head = [Credit(21), Credit(22)];
        CreditPage[] tail = [Credit(23), Credit(24)];

        var found = Find(Pages(1, head, tail, story: 2), Pages(2, head, tail), Pages(3, head, tail));

        Assert.Equal(new CreditPages(2, 0, 0), found);
    }

    [Fact]
    public void A_short_chapter_hides_nothing()
    {
        var credit = Credit(7);

        var found = Find(Pages(1, [credit], [], story: 4), Pages(2, [credit], []), Pages(3, [credit], []));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void An_unreadable_page_ends_the_strip()
    {
        var edge = CreditDetector.Edge.Of([Credit(1), null, Credit(2)], tail: false);

        Assert.Equal(PageRows * (W + 2), edge.Bytes);
    }

    [Fact]
    public void Shrink_counts_each_rows_colour_and_white_pixels()
    {
        // Rows 0 to 9 red, 10 to 19 white, 20 to 29 mid grey, on a page twice the strip's width.
        var pixels = new byte[128 * 30 * 3];
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var (r, g, b) = y < 10 ? (220, 30, 30) : y < 20 ? (255, 255, 255) : (128, 128, 128);
                var at = (y * 128 + x) * 3;
                (pixels[at], pixels[at + 1], pixels[at + 2]) = ((byte)r, (byte)g, (byte)b);
            }
        }

        using var image = NetVips.Image.NewFromMemory(pixels, 128, 30, 3, NetVips.Enums.BandFormat.Uchar);
        var page = CreditDetector.Shrink(image.PngsaveBuffer());

        Assert.Equal(15, page.Height);
        Assert.Equal(W * page.Height, page.Grey.Pixels.Length);
        Assert.Equal(W, page.Colour[1]);
        Assert.Equal(0, page.White[1]);
        Assert.Equal(W, page.White[7]);
        Assert.Equal(0, page.Colour[7]);
        Assert.Equal(0, page.Colour[13]);
        Assert.Equal(0, page.White[13]);
    }

    [Fact]
    public void A_black_and_white_repeated_page_is_kept()
    {
        var colophon = Colophon(7);

        var found = Find(Pages(1, [colophon], []), Pages(2, [colophon], []), Pages(3, [colophon], []));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void In_a_colour_chapter_a_repeated_page_on_a_white_ground_is_kept()
    {
        var title = Logo(5, ground: 255);

        var found = Find(
            Pages(1, [title], [], colour: true),
            Pages(2, [title], [], colour: true),
            Pages(3, [title], [], colour: true));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void In_a_colour_chapter_a_repeated_page_on_a_filled_ground_is_hidden()
    {
        var credit = Logo(5, ground: 120);

        var found = Find(
            Pages(1, [credit], [], colour: true),
            Pages(2, [credit], [], colour: true),
            Pages(3, [credit], [], colour: true));

        Assert.Equal(new CreditPages(1, 0, 0), found);
    }

    [Fact]
    public void An_official_page_at_the_edge_keeps_the_credit_page_behind_it()
    {
        CreditPage[] head = [Colophon(3), Credit(7)];

        var found = Find(Pages(1, head, []), Pages(2, head, []), Pages(3, head, []));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void A_banner_cut_over_grey_rows_is_dropped()
    {
        var banner = Credit(5, 100, colour: false);
        CreditPage[] Webtoon(int seed) =>
        [
            Join(banner, Story(seed, 500, colour: true)),
            .. Enumerable.Range(1, 8).Select(n => Story(seed * 100 + n, 600, colour: true)),
        ];

        var found = Find(Webtoon(1), Webtoon(2), Webtoon(3), Webtoon(4));

        Assert.Equal(CreditPages.None, found);
    }

    [Fact]
    public void Candidates_list_repeated_pages_edge_inward_with_their_colour_verdict()
    {
        var (official, credit) = (Colophon(1), Credit(2));
        var chapter = Pages(10, [official, credit], [credit]);
        var references = new[] { Pages(11, [official, credit], [credit]), Pages(12, [official, credit], [credit]) };

        var candidates = CreditDetector.Candidates(chapter.Length, Chapter(chapter), [.. references.Select(Chapter)]);

        Assert.Equal([new CreditCandidate(0, 1, false), new CreditCandidate(1, 1, true)], candidates.Head);
        Assert.Equal([new CreditCandidate(0, 1, true)], candidates.Tail);
    }

    [Fact]
    public void Settle_hides_the_accepted_run_from_each_edge()
    {
        var (official, credit) = (Colophon(1), Credit(2));
        var chapter = Pages(10, [official, credit], [credit]);
        var references = new[] { Pages(11, [official, credit], [credit]), Pages(12, [official, credit], [credit]) };
        var candidates = CreditDetector.Candidates(chapter.Length, Chapter(chapter), [.. references.Select(Chapter)]);

        Assert.Equal(new CreditPages(2, 0, 1), CreditDetector.Settle(candidates, 2, 1));
        Assert.Equal(new CreditPages(0, 0, 0), CreditDetector.Settle(candidates, 0, 0));
    }

    [Fact]
    public void A_banner_candidate_carries_its_share_and_settles_to_an_offset()
    {
        var banner = Credit(5, 100);
        CreditPage[] Webtoon(int seed) =>
            [Join(banner, Story(seed, 500)), .. Enumerable.Range(1, 8).Select(n => Story(seed * 100 + n, 600))];
        var chapter = Webtoon(1);

        var candidates = CreditDetector.Candidates(chapter.Length, Chapter(chapter), [Chapter(Webtoon(2)), Chapter(Webtoon(3))]);

        var only = Assert.Single(candidates.Head);
        Assert.True(only.Share is > 0 and < 1);
        Assert.Equal(Math.Round(only.Share, 4), CreditDetector.Settle(candidates, 1, 0).HeadOffset);
        Assert.Equal(CreditPages.None, CreditDetector.Settle(candidates, 0, 0));
    }

    /// <summary>A webtoon chapter whose last slice ends in <paramref name="banner"/>, with <paramref name="after"/> behind it.</summary>
    private static CreditPage[] EndingIn(int seed, CreditPage banner, params CreditPage[] after) =>
        [.. Enumerable.Range(1, 8).Select(n => Story(seed * 100 + n, 600)), Join(Story(seed, 500), banner), .. after];

    [Fact]
    public void A_banner_fused_into_the_bottom_of_the_last_slice_gives_its_bottom_share()
    {
        var banner = Credit(5, 100);
        var chapter = EndingIn(1, banner);

        var candidates = CreditDetector.Candidates(chapter.Length, Chapter(chapter), [Chapter(EndingIn(2, banner)), Chapter(EndingIn(3, banner))]);
        var found = Find(chapter, EndingIn(2, banner), EndingIn(3, banner), EndingIn(4, banner));

        var only = Assert.Single(candidates.Tail);
        Assert.Equal(0, only.Page);
        Assert.InRange(only.Share, 100 / 600.0 - 0.02, 100 / 600.0 + 0.005);
        Assert.Empty(candidates.Head);
        Assert.Equal(0, found.TailPages);
        Assert.Equal(Math.Round(only.Share, 4), found.TailOffset);
        Assert.Equal(0, found.HeadOffset);
    }

    [Fact]
    public void Whole_tail_pages_come_before_the_bottom_of_the_slice_behind_them()
    {
        var (banner, notice) = (Credit(5, 100), Credit(6, 100));
        var chapter = EndingIn(1, banner, notice);

        var candidates = CreditDetector.Candidates(
            chapter.Length, Chapter(chapter), [Chapter(EndingIn(2, banner, notice)), Chapter(EndingIn(3, banner, notice))]);

        Assert.Equal(2, candidates.Tail.Count);
        Assert.Equal(new CreditCandidate(0, 1, true), candidates.Tail[0]);
        Assert.Equal(1, candidates.Tail[1].Page);
        Assert.InRange(candidates.Tail[1].Share, 100 / 600.0 - 0.02, 100 / 600.0 + 0.005);
        Assert.Equal(new CreditPages(0, 0, 1, Math.Round(candidates.Tail[1].Share, 4)), CreditDetector.Settle(candidates, 0, 2));
        Assert.Equal(new CreditPages(0, 0, 1), CreditDetector.Settle(candidates, 0, 1));
    }

    [Fact]
    public void A_bank_entry_proposes_no_bottom_share_of_a_tail()
    {
        var banner = Credit(5, 100);
        var source = Chapter(EndingIn(1, banner));
        var lone = EndingIn(2, banner);

        Assert.Empty(CreditDetector.BankCandidates(lone.Length, Chapter(lone), source.Tail, tail: true));
    }

    [Fact]
    public void Settle_cuts_the_tail_only_behind_every_whole_page_it_keeps()
    {
        CreditCandidate Whole(int page) => new(page, 1, true);
        var tail = new List<CreditCandidate> { Whole(0), Whole(1), Whole(2), new(3, 0.3, true) };

        Assert.Equal(new CreditPages(0, 0, 3, 0.3), CreditDetector.Settle(new CreditCandidates(30, [], tail), 0, 4));
        // The head takes the third the chapter allows first: the tail loses whole pages, and the cut with them.
        Assert.Equal(new CreditPages(2, 0, 1), CreditDetector.Settle(new CreditCandidates(9, [Whole(0), Whole(1)], tail), 2, 4));
        // A cut this close to the bottom is not worth making.
        Assert.Equal(new CreditPages(0, 0, 0), CreditDetector.Settle(new CreditCandidates(30, [], [new(0, 0.02, true)]), 0, 1));
        Assert.Equal(new CreditPages(0, 0, 0, 0.3), CreditDetector.Settle(new CreditCandidates(30, [], [new(0, 0.3, true)]), 0, 1));
    }
}
