using Maki.Api.Services;
using Maki.Metadata.Embedding;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="DiscoverService.OrderRows"/>: the newest/oldest release-date orderings and the rating
/// popularity gate. Internal, reachable here through Maki.Metadata's InternalsVisibleTo grant.
/// </summary>
public class DiscoverServiceOrderRowsTests
{
    private const int Dim = 4;

    [Fact]
    public void Newest_sorts_by_start_day_descending()
    {
        var today = new DateOnly(2026, 9, 23).DayNumber;
        var index = Build(
            ids: [100, 101, 102],
            startDays:
            [
                new DateOnly(2010, 1, 1).DayNumber,
                new DateOnly(2020, 6, 15).DayNumber,
                new DateOnly(2015, 3, 1).DayNumber,
            ],
            popularity: [1, 2, 3]);

        var ordered = DiscoverService.OrderRows(index, FilterPlan.None, BrowseSort.Newest, 0, 10, today);

        Assert.Equal([101L, 102L, 100L], ordered);
    }

    [Fact]
    public void Oldest_sorts_by_start_day_ascending()
    {
        var today = new DateOnly(2026, 9, 23).DayNumber;
        var index = Build(
            ids: [100, 101, 102],
            startDays:
            [
                new DateOnly(2010, 1, 1).DayNumber,
                new DateOnly(2020, 6, 15).DayNumber,
                new DateOnly(2015, 3, 1).DayNumber,
            ],
            popularity: [1, 2, 3]);

        var ordered = DiscoverService.OrderRows(index, FilterPlan.None, BrowseSort.Oldest, 0, 10, today);

        Assert.Equal([100L, 102L, 101L], ordered);
    }

    [Fact]
    public void Newest_sorts_an_announced_future_release_after_every_released_title()
    {
        var today = new DateOnly(2026, 9, 23).DayNumber;
        var index = Build(
            ids: [100, 101, 102],
            startDays:
            [
                new DateOnly(2010, 1, 1).DayNumber,
                new DateOnly(2020, 6, 15).DayNumber,
                new DateOnly(2030, 1, 1).DayNumber, // announced, not released yet
            ],
            popularity: [1, 2, 3]);

        var ordered = DiscoverService.OrderRows(index, FilterPlan.None, BrowseSort.Newest, 0, 10, today);

        Assert.Equal(102L, ordered[^1]);
    }

    [Fact]
    public void Newest_sorts_an_undated_row_after_every_released_title_too()
    {
        var today = new DateOnly(2026, 9, 23).DayNumber;
        var index = Build(
            ids: [100, 101],
            startDays: [new DateOnly(2010, 1, 1).DayNumber, VectorIndex.Unknown],
            popularity: [1, 2]);

        var ordered = DiscoverService.OrderRows(index, FilterPlan.None, BrowseSort.Newest, 0, 10, today);

        Assert.Equal([100L, 101L], ordered);
    }

    [Fact]
    public void Rating_puts_a_row_below_the_popularity_gate_after_every_gated_row_regardless_of_rating()
    {
        var index = Build(
            ids: [100, 101],
            ratings: [95f, 60f],
            // Row 0's rating is far higher, but it sits below the gate: one enthusiastic vote must
            // not outrank a broadly-rated, popular title.
            popularity: [20000, 500]);

        var ordered = DiscoverService.OrderRows(index, FilterPlan.None, BrowseSort.Rating, 0, 10);

        Assert.Equal([101L, 100L], ordered);
    }

    [Fact]
    public void Rating_orders_gated_rows_by_rating_then_by_popularity_on_a_tie()
    {
        var index = Build(
            ids: [100, 101, 102],
            ratings: [70f, 90f, 90f],
            popularity: [1, 5, 3]);

        var ordered = DiscoverService.OrderRows(index, FilterPlan.None, BrowseSort.Rating, 0, 10);

        // 101 and 102 both rate 90; the more popular (lower rank number) of the two leads.
        Assert.Equal([102L, 101L, 100L], ordered);
    }

    /// <summary>Builds a minimal index over unit vectors, ids by position.</summary>
    private static VectorIndex Build(
        long[] ids, int[]? startDays = null, int[]? years = null, float[]? ratings = null, int[]? popularity = null)
    {
        var count = ids.Length;
        var data = new sbyte[count * Dim];
        var scales = new float[count];
        for (var i = 0; i < count; i++)
        {
            var vector = new float[Dim];
            vector[i % Dim] = 1f;
            scales[i] = EmbeddingMath.Quantize(vector, data.AsSpan(i * Dim, Dim));
        }

        return VectorIndex.FromQuantized(
            ids,
            data,
            scales,
            Dim,
            new VectorIndexColumns(
                Years: years ?? Enumerable.Repeat(VectorIndex.Unknown, count).ToArray(),
                Ratings: ratings ?? Enumerable.Repeat(75f, count).ToArray(),
                Chapters: Enumerable.Repeat(100, count).ToArray(),
                Types: new byte[count],
                Statuses: new byte[count],
                Genres: JaggedInts.From(new int[count][]),
                Authors: JaggedInts.From(new int[count][]),
                Artists: JaggedInts.From(new int[count][]),
                Popularity: popularity ?? Enumerable.Repeat(VectorIndex.Unknown, count).ToArray(),
                TagBlobs: new byte[]?[count],
                ContentRatings: new byte[count],
                Franchise: Enumerable.Repeat(VectorIndex.Unknown, count).ToArray(),
                StartDays: startDays),
            new VectorIndexVocabularies(
                new Dictionary<string, byte>(), new Dictionary<string, byte>(),
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                new Dictionary<string, int[]>(), new Dictionary<string, byte>()));
    }
}
