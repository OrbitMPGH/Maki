using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>
/// Lining the same page up across sources that don't agree on where a chapter starts.
/// <para>
/// Hashes here are hand-built rather than computed from images: what matters is the bit distance
/// between them, and writing that down directly is the only way to test the decision boundary at
/// all. <see cref="Near"/> perturbs a hash by an exact number of bits, which is what a second site's
/// re-encode of the same page looks like.
/// </para>
/// </summary>
public class PageAlignmentTests
{
    /// <summary>A distinct page. Spread out so two unrelated pages sit ~32 bits apart, as real ones do.</summary>
    private static ulong Page(int seed)
    {
        // xorshift64*, so consecutive seeds give uncorrelated hashes rather than adjacent ones.
        var x = (ulong)(seed + 1) * 0x9E3779B97F4A7C15UL;
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        return x;
    }

    /// <summary>The same page as another site serves it: identical drawing, <paramref name="bits"/> apart.</summary>
    private static ulong Near(int seed, int bits)
    {
        var hash = Page(seed);
        for (var i = 0; i < bits; i++)
        {
            hash ^= 1UL << (i * 3 % 64);
        }
        return hash;
    }

    /// <summary>A page exactly 32 bits from <paramref name="other"/> — two unrelated drawings.</summary>
    private static ulong Unlike(ulong other) => other ^ 0xF0F0F0F0F0F0F0F0UL;

    private static IReadOnlyList<ulong> Pages(params ulong[] hashes) => hashes;

    [Fact]
    public void Identical_sources_line_up_as_they_are()
    {
        var a = Pages(Page(1), Page(2), Page(3), Page(4));
        var slots = PageAlignment.Align([a, a], 3);

        Assert.Equal(3, slots.Count);
        Assert.Equal([0, 0], slots[0]);
        Assert.Equal([1, 1], slots[1]);
        Assert.Equal([2, 2], slots[2]);
    }

    [Fact]
    public void A_leading_credit_page_is_skipped()
    {
        // The whole point: one source opens with a credit page, so its page 1 is everyone else's
        // page 0. Comparing raw indexes would put a credits screen next to artwork.
        var plain = Pages(Page(1), Page(2), Page(3), Page(4), Page(5));
        var withCredits = Pages(Page(99), Near(1, 5), Near(2, 6), Near(3, 4), Near(4, 5));

        var slots = PageAlignment.Align([plain, withCredits], 3);

        Assert.Equal([0, 1], slots[0]);
        Assert.Equal([1, 2], slots[1]);
        Assert.Equal([2, 3], slots[2]);
    }

    [Fact]
    public void Two_leading_pages_are_skipped()
    {
        // Measured on a real series: MangaDex led with a content warning *and* a cover the other
        // sites didn't carry.
        var plain = Pages(Page(1), Page(2), Page(3), Page(4), Page(5), Page(6));
        var withExtras = Pages(Page(98), Page(99), Near(1, 6), Near(2, 5), Near(3, 7), Near(4, 6));

        var slots = PageAlignment.Align([plain, withExtras], 3);

        Assert.Equal([0, 2], slots[0]);
        Assert.Equal([1, 3], slots[1]);
        Assert.Equal([2, 4], slots[2]);
    }

    [Fact]
    public void A_source_carrying_a_different_release_is_left_where_it_is()
    {
        // Different scanlation group: nothing matches at any offset, and with nobody else to copy
        // the honest answer is the raw order.
        var known = Pages(Page(1), Page(2), Page(3), Page(4), Page(5));
        var unrelated = Pages(Page(50), Page(51), Page(52), Page(53), Page(54));

        var slots = PageAlignment.Align([known, unrelated], 3);

        Assert.Equal([0, 0], slots[0]);
        Assert.Equal([1, 1], slots[1]);
        Assert.Equal([2, 2], slots[2]);
    }

    [Fact]
    public void An_unmatched_source_follows_what_the_others_agreed_on()
    {
        // The reference is the one with two extra leading pages, so leaving the odd source at the
        // reference's own indexes would misalign it by exactly those two. Everyone who *did* match
        // said -2, so the source nobody could place gets -2 as well.
        var withExtras = Pages(Page(90), Page(91), Page(1), Page(2), Page(3), Page(4), Page(5));
        var plainA = Pages(Near(1, 5), Near(2, 6), Near(3, 4), Near(4, 5), Near(5, 6));
        var plainB = Pages(Near(1, 6), Near(2, 4), Near(3, 6), Near(4, 4), Near(5, 5));
        var otherRelease = Pages(Page(60), Page(61), Page(62), Page(63), Page(64));

        var slots = PageAlignment.Align([withExtras, plainA, plainB, otherRelease], 3);

        // Reference index 2 is the first real page; the other three all sit two earlier.
        Assert.Equal([2, 0, 0, 0], slots[0]);
        Assert.Equal([3, 1, 1, 1], slots[1]);
        Assert.Equal([4, 2, 2, 2], slots[2]);
    }

    [Fact]
    public void Front_matter_neither_release_shares_is_skipped()
    {
        // Two releases of one chapter that converge rather than align: one opens with a translator's
        // preface, the other with a title page, and neither carries the other's. No single offset
        // can match those, so the comparison has to start at the first page they agree on.
        var withPreface = Pages(Page(80), Page(1), Page(2), Page(3), Page(4));
        var withTitle = Pages(Unlike(Page(80)), Near(1, 6), Near(2, 5), Near(3, 6), Near(4, 5));

        var slots = PageAlignment.Align([withPreface, withTitle], 3);

        // Index 0 in each is the unshared front matter; the rows returned start after it.
        Assert.All(slots, row => Assert.True(row[0] > 0 && row[1] > 0));
        Assert.Equal([1, 1], slots[0]);
        Assert.Equal([2, 2], slots[1]);
    }

    [Fact]
    public void Three_sources_each_get_their_own_offset()
    {
        var plain = Pages(Page(1), Page(2), Page(3), Page(4), Page(5), Page(6));
        var oneExtra = Pages(Page(90), Near(1, 4), Near(2, 5), Near(3, 4), Near(4, 6), Near(5, 5));
        var twoExtra = Pages(Page(91), Page(92), Near(1, 6), Near(2, 4), Near(3, 5), Near(4, 4));

        var slots = PageAlignment.Align([plain, oneExtra, twoExtra], 3);

        Assert.Equal([0, 1, 2], slots[0]);
        Assert.Equal([1, 2, 3], slots[1]);
        Assert.Equal([2, 3, 4], slots[2]);
    }

    [Fact]
    public void Slots_stop_where_the_shortest_shifted_source_runs_out()
    {
        // Asking for four slots when the shifted panel only reaches three must not hand back a row
        // pointing past the end of its page list.
        var plain = Pages(Page(1), Page(2), Page(3), Page(4));
        var withCredit = Pages(Page(99), Near(1, 5), Near(2, 4), Near(3, 6));

        var slots = PageAlignment.Align([plain, withCredit], 4);

        Assert.Equal(3, slots.Count);
        Assert.All(slots, row => Assert.True(row[1] < 4));
    }

    [Fact]
    public void A_panel_with_no_pages_yields_nothing()
    {
        // Callers filter failed panels out, but an empty list must not produce rows that index them.
        Assert.Empty(PageAlignment.Align([Pages(Page(1), Page(2)), Pages()], 3));
    }

    [Fact]
    public void Sources_too_short_to_judge_are_left_alone()
    {
        // Two pages each is under the overlap floor at every offset, so there is nothing to measure
        // and the honest answer is the raw order.
        var a = Pages(Page(1), Page(2));
        var b = Pages(Page(99), Near(1, 5));

        var slots = PageAlignment.Align([a, b], 2);

        Assert.Equal([0, 0], slots[0]);
        Assert.Equal([1, 1], slots[1]);
    }

    [Fact]
    public void Real_measured_distances_align_one_pair_and_decline_the_other()
    {
        // Distances taken from a live four-source comparison of Boy Meets Maria chapter 1.
        // MangaPill vs MangaDex scored 12.75 at shift -2 against 28.5 at shift 0 — aligned.
        // TopManhua (a different release) never beat ~22 at any offset — declined.
        Assert.Equal(-2, ShiftOf(28.5, -2, 12.75));
        Assert.Equal(0, ShiftOf(27.5, -3, 22.0));
    }

    /// <summary>
    /// Builds two sequences whose mean distance is <paramref name="atZero"/> when unshifted and
    /// <paramref name="atShift"/> at <paramref name="shift"/>, then reports what Align decided.
    /// </summary>
    private static int ShiftOf(double atZero, int shift, double atShift)
    {
        const int length = 6;
        var reference = new ulong[length];
        var other = new ulong[length];
        for (var i = 0; i < length; i++)
        {
            reference[i] = Page(i);
        }

        // Fill `other` so that other[i + shift] is a near-copy of reference[i], and everything else
        // is far from everything.
        for (var i = 0; i < length; i++)
        {
            other[i] = Page(500 + i);
        }
        for (var r = 0; r < length; r++)
        {
            var index = r + shift;
            if (index >= 0 && index < length)
            {
                other[index] = Near(r, (int)Math.Round(atShift));
            }
        }

        var slots = PageAlignment.Align([reference, other], 1);
        Assert.NotEmpty(slots);

        // atZero is only meaningful as "unrelated pages", which the filler above already produces.
        Assert.True(atZero > atShift || shift == 0);
        return slots[0][1] - slots[0][0];
    }
}
