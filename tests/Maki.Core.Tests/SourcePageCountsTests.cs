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
}
