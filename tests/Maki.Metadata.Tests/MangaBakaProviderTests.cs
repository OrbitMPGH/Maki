using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

public class MangaBakaProviderTests
{
    private static MangaBakaProvider Provider(string dbPath) => new(new MangaBakaLocalStore(
        new MangaBakaDumpOptions(dbPath, Path.GetTempPath()), NullLogger<MangaBakaLocalStore>.Instance));

    [Fact]
    public async Task Without_the_dump_search_is_empty_and_get_is_null()
    {
        var provider = Provider(Path.Combine(Path.GetTempPath(), $"maki-missing-{Guid.NewGuid():N}.db"));

        Assert.Empty(await provider.SearchAsync("one piece", ContentRating.Pornographic));
        Assert.Null(await provider.GetAsync("377"));
    }

    [Fact]
    public async Task With_the_dump_it_answers_from_it()
    {
        using var dump = new DumpDbBuilder();
        dump.AddSeries(377, "ONE PIECE").BuildSearchIndex();
        var provider = Provider(dump.Path);

        Assert.Equal("377", Assert.Single(await provider.SearchAsync("one piece", ContentRating.Pornographic)).ProviderId);
        Assert.Equal("ONE PIECE", (await provider.GetAsync("377"))?.Title);
    }

    [Fact]
    public async Task HasSeries_counts_a_novel_GetAsync_declines_but_not_an_id_the_dump_lacks()
    {
        using var dump = new DumpDbBuilder();
        dump.AddSeries(500, "A Light Novel", type: "novel");
        var store = new MangaBakaLocalStore(
            new MangaBakaDumpOptions(dump.Path, Path.GetTempPath()), NullLogger<MangaBakaLocalStore>.Instance);

        Assert.Null(await store.GetAsync("500"));
        Assert.True(await store.HasSeriesAsync(500));
        Assert.False(await store.HasSeriesAsync(501));
    }
}
