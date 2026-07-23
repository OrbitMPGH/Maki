using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Covers title normalization and the auto-mapping rules in <see cref="SourceMatchService"/>:
/// title-similarity matching (<see cref="Maki.Core.Scrobbling.ScrobbleMatching"/>) against both
/// title and original title, subtitle-variant acceptance, and the guards that leave a series
/// unmapped (no match above threshold, already mapped, source error).
/// </summary>
public class SourceMatchServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("Hajime no Ippo", "hajimenoippo")]
    [InlineData("Attack on Titan!", "attackontitan")]
    [InlineData("JoJo's Bizarre Adventure", "jojosbizarreadventure")]
    [InlineData("  Spaced  Out  ", "spacedout")]
    public void Normalize_strips_non_alphanumeric_and_lowercases(string input, string expected) =>
        Assert.Equal(expected, SourceMatchService.Normalize(input));

    private async Task<List<string>> RunAutoMatch(int seriesId, params ISource[] sources)
    {
        var context = _db.NewContext();
        var series = await context.Series.Include(s => s.SourceMappings).FirstAsync(s => s.Id == seriesId);
        var service = new SourceMatchService(
            context, new SourceRegistry(sources), new FakeAppSettings(),
            NullLogger<SourceMatchService>.Instance);
        return await service.AutoMatchAsync(series);
    }

    private static SourceSeriesResult Hit(string title) =>
        new(SourceSeriesId: "sid", Title: title, Url: "https://x.test/s");

    private List<SourceMapping> MappingsOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.SourceMappings.Where(m => m.SeriesId == seriesId).ToList();
    }

    [Fact]
    public async Task Exact_normalized_match_creates_a_mapping()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("HAJIME NO IPPO")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
        Assert.Equal("sid", Assert.Single(MappingsOf(seriesId)).SourceSeriesId);
    }

    [Fact]
    public async Task Original_title_is_also_matched()
    {
        var seriesId = _db.SeedSeries("Attack on Titan", originalTitle: "Shingeki no Kyojin");
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("Shingeki no Kyojin")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
    }

    [Fact]
    public async Task Subtitle_variant_is_accepted()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("Hajime no Ippo: Fighting Spirit!")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
    }

    [Fact]
    public async Task Unrelated_title_is_left_unmapped()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("Berserk")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Empty(mapped);
        Assert.Empty(MappingsOf(seriesId));
    }

    [Fact]
    public async Task Franchise_root_result_does_not_win_over_no_match()
    {
        // Regression: a gaiden/spin-off's title only partially overlaps the franchise
        // root name a source returns for it ("Naruto") - similarity must stay below
        // threshold so it isn't mapped to the unrelated parent series.
        var seriesId = _db.SeedSeries("Naruto Gaiden: The Seventh Hokage");
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("Naruto")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Empty(mapped);
        Assert.Empty(MappingsOf(seriesId));
    }

    [Fact]
    public async Task Generic_original_title_does_not_falsely_match_the_parent_series()
    {
        // Regression: MangaBaka often gives spin-offs/one-shots an OriginalTitle that's
        // just the franchise banner ("NARUTO"), which can exactly equal an unrelated
        // parent series' title in a source's search results.
        var seriesId = _db.SeedSeries(
            "Naruto: The Seventh Hokage and the Scarlet Spring", originalTitle: "NARUTO");
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("Naruto")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Empty(mapped);
        Assert.Empty(MappingsOf(seriesId));
    }

    [Fact]
    public async Task Already_mapped_source_is_skipped_without_searching()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", mappings: new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = "existing", Url = "https://fake.test/s"
        });
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("Hajime no Ippo")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Empty(mapped);
        Assert.Equal(0, source.SearchCalls);
        Assert.Equal("existing", Assert.Single(MappingsOf(seriesId)).SourceSeriesId);
    }

    [Fact]
    public async Task Source_error_is_swallowed_and_leaves_the_series_unmapped()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var throwing = new FakeSource
        {
            Name = "boom",
            OnSearch = _ => throw new InvalidOperationException("down")
        };
        var ok = new FakeSource { Name = "ok", OnSearch = _ => [Hit("Hajime no Ippo")] };

        var mapped = await RunAutoMatch(seriesId, throwing, ok);

        // The throwing source is tolerated; the healthy one still maps.
        Assert.Equal(["ok"], mapped);
    }
}
