using Maki.Metadata.MangaBaka;

namespace Maki.Metadata.Tests;

public class ScoreStandingsTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private ScoreStandings Standings(int minGroupSize = 3) =>
        new(new MangaBakaDumpOptions(_db.Path, Path.GetTempPath()), minGroupSize);

    /// <summary>Ten manga ranked 1 to 10 by popularity, the popular two scoring 80 and 70.</summary>
    private void SeedManga(int count = 10)
    {
        for (var i = 1; i <= count; i++)
        {
            _db.AddSeries(i, $"Manga {i}", type: "manga", rating: i switch { 1 => 80, 2 => 70, _ => 40 }, popularityType: i);
        }
    }

    [Fact]
    public async Task Compares_against_the_most_popular_fifth_of_the_type()
    {
        SeedManga(15);

        var standing = await Standings().GetAsync("manga", 75);

        Assert.NotNull(standing);
        Assert.Equal("manga", standing.Group);
        Assert.Equal(3, standing.GroupSize);
        Assert.Equal(200.0 / 3, standing.Percentile, 6);
    }

    [Fact]
    public async Task A_type_with_too_few_popular_series_falls_back_to_all_of_them()
    {
        SeedManga(15);
        _db.AddSeries(100, "Lone OEL", type: "oel", rating: 90, popularityType: 1);

        var standing = await Standings().GetAsync("oel", 90);

        Assert.NotNull(standing);
        Assert.Equal(ScoreStandings.AllGroup, standing.Group);
        Assert.Equal(4, standing.GroupSize);
        Assert.Equal(75, standing.Percentile, 6);
    }

    [Fact]
    public async Task Novels_merged_rows_and_unranked_series_are_left_out()
    {
        SeedManga(15);
        _db.AddSeries(200, "Novel", type: "novel", rating: 99, popularityType: 1);
        _db.AddSeries(201, "Merged", state: "merged", mergedWith: "1", type: "manga", rating: 99, popularityType: 1);
        _db.AddSeries(202, "Unranked", type: "manga", rating: 99);

        var standing = await Standings().GetAsync("manga", 99);

        Assert.NotNull(standing);
        Assert.Equal(3, standing.GroupSize);
        Assert.Equal(100, standing.Percentile, 6);
    }

    [Fact]
    public async Task The_histogram_counts_half_point_bins_from_five_and_skips_lower_scores()
    {
        SeedManga(15);

        var standing = await Standings().GetAsync("manga", 70);

        Assert.NotNull(standing);
        Assert.Equal([0, 0, 0, 0, 1, 0, 1, 0, 0, 0], standing.Histogram);
    }

    [Fact]
    public async Task Shares_count_popular_series_at_or_above_each_point()
    {
        SeedManga(15);

        var shares = await Standings().GetSharesAsync();

        Assert.NotNull(shares);
        Assert.Equal(3, shares.GroupSize);
        Assert.Equal(101, shares.AtOrAbove.Count);
        Assert.Equal(100, shares.AtOrAbove[40], 6);
        Assert.Equal(200.0 / 3, shares.AtOrAbove[41], 6);
        Assert.Equal(200.0 / 3, shares.AtOrAbove[70], 6);
        Assert.Equal(100.0 / 3, shares.AtOrAbove[71], 6);
        Assert.Equal(0, shares.AtOrAbove[81], 6);
    }

    [Fact]
    public async Task An_unrated_series_has_no_standing()
    {
        SeedManga();

        Assert.Null(await Standings().GetAsync("manga", null));
    }

    [Fact]
    public async Task A_replaced_dump_is_read_again()
    {
        SeedManga(15);
        var standings = Standings();
        Assert.Equal(3, (await standings.GetAsync("manga", 75))!.GroupSize);

        for (var i = 16; i <= 30; i++)
        {
            _db.AddSeries(i, $"Manga {i}", type: "manga", rating: 50, popularityType: i);
        }

        File.SetLastWriteTimeUtc(_db.Path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(6, (await standings.GetAsync("manga", 75))!.GroupSize);
    }
}
