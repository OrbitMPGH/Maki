using Maki.Core.Quality;

namespace Maki.Core.Tests;

public class SourcePageCountsTests
{
    private static SourcePageCounts.Sample S(int mapping, int chapter, int pages) => new(mapping, chapter, pages);

    [Fact]
    public void A_source_that_is_much_shorter_than_the_others_on_the_same_chapters_is_truncated()
    {
        var samples = new[]
        {
            S(1, 1, 10), S(1, 2, 10), S(1, 3, 11),
            S(2, 1, 40), S(2, 2, 48), S(2, 3, 52),
            S(3, 1, 41), S(3, 2, 47), S(3, 3, 50)
        };

        Assert.Equal([1], SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void One_short_chapter_is_not_enough()
    {
        var samples = new[] { S(1, 1, 8), S(1, 2, 44), S(2, 1, 40), S(2, 2, 45) };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void A_source_a_little_shorter_is_not_truncated()
    {
        var samples = new[] { S(1, 1, 36), S(1, 2, 40), S(2, 1, 40), S(2, 2, 44) };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void Three_chapters_of_one_short_fixed_length_look_like_a_preview_cap()
    {
        var samples = new[] { S(1, 1, 10), S(1, 2, 10), S(1, 3, 10), S(2, 1, 44), S(2, 5, 31) };

        Assert.Equal([1], SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void A_series_of_short_chapters_every_source_agrees_on_is_not_a_preview_cap()
    {
        var samples = new[]
        {
            S(1, 1, 12), S(1, 2, 12), S(1, 3, 13),
            S(2, 1, 12), S(2, 2, 13), S(2, 3, 13)
        };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void A_short_fixed_length_with_no_longer_copy_anywhere_is_left_alone() =>
        Assert.Empty(SourcePageCounts.Truncated([S(1, 1, 10), S(1, 2, 10), S(1, 3, 10)]));

    [Fact]
    public void Chapters_of_varying_length_are_not_a_preview_cap()
    {
        var samples = new[] { S(1, 1, 10), S(1, 2, 14), S(1, 3, 9), S(1, 4, 12) };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void A_fixed_length_that_is_long_is_just_a_regular_source()
    {
        var samples = new[] { S(1, 1, 30), S(1, 2, 30), S(1, 3, 31) };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void A_source_with_nothing_to_compare_and_few_samples_is_left_alone() =>
        Assert.Empty(SourcePageCounts.Truncated([S(1, 1, 3), S(1, 2, 3)]));

    private static SourcePageCounts.Sample Sized(int mapping, int chapter, int pages, int width, int height) =>
        new(mapping, chapter, pages, width, height);

    [Fact]
    public void A_long_strip_cut_into_fewer_taller_images_is_not_short()
    {
        var samples = new[]
        {
            Sized(1, 1, 12, 800, 10_000), Sized(1, 2, 11, 800, 10_000), Sized(1, 3, 12, 800, 10_000),
            Sized(2, 1, 60, 800, 2_000), Sized(2, 2, 56, 800, 2_000), Sized(2, 3, 61, 800, 2_000)
        };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void Long_strip_slices_of_a_steady_count_are_not_a_preview_cap()
    {
        var samples = new[] { Sized(1, 1, 10, 720, 9_000), Sized(1, 2, 10, 720, 9_000), Sized(1, 3, 11, 720, 9_000) };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void A_low_resolution_source_with_every_page_is_not_short()
    {
        var samples = new[]
        {
            Sized(1, 1, 40, 600, 900), Sized(1, 2, 44, 600, 900),
            Sized(2, 1, 40, 1400, 2100), Sized(2, 2, 44, 1400, 2100)
        };

        Assert.Empty(SourcePageCounts.Truncated(samples));
    }

    [Fact]
    public void A_preview_is_short_by_length_too()
    {
        var samples = new[]
        {
            Sized(1, 1, 6, 1000, 1500), Sized(1, 2, 6, 1000, 1500),
            Sized(2, 1, 40, 1000, 1500), Sized(2, 2, 44, 1000, 1500)
        };

        Assert.Equal([1], SourcePageCounts.Truncated(samples));
    }
}
