using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class ChapterOrderTests
{
    private record Row(int Id, decimal? Number, int? Volume);

    private static int[] Order(params Row[] rows) =>
        [.. ChapterOrder.Sort(rows, r => r.Number, r => r.Volume, r => r.Id).Select(r => r.Id)];

    [Fact]
    public void Chapter_zero_of_a_later_volume_sorts_at_the_start_of_that_volume()
    {
        var order = Order(
            new Row(1, 0m, 9),
            new Row(2, 1m, 1),
            new Row(3, 102m, 8),
            new Row(4, 103m, 9),
            new Row(5, 104m, 9));

        Assert.Equal([2, 3, 1, 4, 5], order);
    }

    [Fact]
    public void A_prologue_chapter_zero_still_sorts_first()
    {
        Assert.Equal([1, 2, 3], Order(new Row(2, 1m, 1), new Row(1, 0m, null), new Row(3, 2m, 1)));
        Assert.Equal([1, 2, 3], Order(new Row(2, 1m, 1), new Row(1, 0m, 1), new Row(3, 2m, 1)));
    }

    [Fact]
    public void A_volume_with_no_numbered_chapters_sorts_between_the_volumes_around_it()
    {
        // Volume 1 starts at 1 and volume 16 at 124.5, so volume 3 is estimated to start near 17.5.
        var order = Order(
            new Row(1, 0m, 3),
            new Row(2, 1m, 1),
            new Row(3, 3m, 1),
            new Row(4, 17m, null),
            new Row(5, 18m, null),
            new Row(6, 124.5m, 16));

        Assert.Equal([2, 3, 4, 1, 5, 6], order);
    }

    [Fact]
    public void A_volume_below_every_known_volume_counts_from_the_start_of_the_series()
    {
        // Volume 5 starts at 40, so volume 3 is half way from the start.
        var order = Order(new Row(1, 0m, 3), new Row(2, 1m, null), new Row(3, 25m, null), new Row(4, 40m, 5));

        Assert.Equal([2, 1, 3, 4], order);
    }

    [Fact]
    public void A_volume_past_every_known_volume_follows_the_last_of_them()
    {
        var order = Order(
            new Row(1, 0m, 9),
            new Row(2, 1m, 1),
            new Row(3, 80m, 8),
            new Row(4, 81m, null),
            new Row(5, 82m, null));

        Assert.Equal([2, 3, 1, 4, 5], order);
    }

    [Fact]
    public void A_volume_upload_on_a_series_without_volumes_sorts_after_every_chapter()
    {
        var order = Order(new Row(1, 0m, 10), new Row(2, 1m, null), new Row(3, 2m, null), new Row(4, null, null));

        Assert.Equal([2, 3, 1, 4], order);
    }

    [Fact]
    public void One_shots_sort_last()
    {
        Assert.Equal([2, 3, 1], Order(new Row(1, null, null), new Row(2, 1m, null), new Row(3, 2m, null)));
    }
}
