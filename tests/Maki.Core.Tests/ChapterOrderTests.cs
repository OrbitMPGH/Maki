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
    public void One_shots_sort_last()
    {
        Assert.Equal([2, 3, 1], Order(new Row(1, null, null), new Row(2, 1m, null), new Row(3, 2m, null)));
    }
}
