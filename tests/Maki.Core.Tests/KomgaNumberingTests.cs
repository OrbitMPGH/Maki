using Maki.Core.Komga;

namespace Maki.Core.Tests;

public class KomgaNumberingTests
{
    [Fact]
    public void Numbered_chapters_keep_their_own_number()
    {
        Assert.Equal([1, 2, 2.5, 3], KomgaNumbering.NumberSorts([1m, 2m, 2.5m, 3m]));
    }

    [Fact]
    public void Unnumbered_chapters_follow_the_last_number()
    {
        // One-shots sort last in reading order, so they continue the sequence instead of jumping to 0.
        Assert.Equal([1, 2, 3, 4], KomgaNumbering.NumberSorts([1m, 2m, null, null]));
        Assert.Equal([1, 2], KomgaNumbering.NumberSorts([null, null]));
    }

    [Fact]
    public void A_chapter_placed_after_a_higher_number_never_steps_back()
    {
        // ChapterOrder puts a volume's chapter 0 at the start of its volume, after the previous volume's chapters.
        Assert.Equal([80, 80, 81], KomgaNumbering.NumberSorts([80m, 0m, 81m]));
    }

    [Fact]
    public void Continuous_read_stops_at_the_first_gap()
    {
        Assert.Equal(2, KomgaNumbering.LastContinuousRead([(1, true), (2, true), (3, false), (4, true)]));
        Assert.Equal(0, KomgaNumbering.LastContinuousRead([(1, false), (2, true)]));
    }

    [Fact]
    public void A_chapter_read_in_one_language_is_read()
    {
        Assert.Equal(2, KomgaNumbering.LastContinuousRead([(1, true), (2, false), (2, true), (3, false)]));
    }

    [Fact]
    public void Newly_read_takes_only_unread_groups_up_to_the_mark()
    {
        var books = new List<(double, bool)> { (1, true), (2, false), (2, false), (3, false), (4, false) };

        Assert.Equal([2, 3], KomgaNumbering.NewlyRead(books, 3));
        Assert.Empty(KomgaNumbering.NewlyRead(books, 1));
    }
}
