using Maki.Core.Sources;
using Maki.Sources.MangaDex;

namespace Maki.Sources.Tests;

/// <summary>
/// MangaDex is the one source whose search response already carries tracker ids, so its results fill
/// <see cref="SourceSeriesResult.ExternalIds"/> directly and it never needs a per-candidate lookup.
/// </summary>
public class MangaDexSourceTests
{
    private static MangaDexSource WithSearch() =>
        new(new FakeHttpClientFactory(new()
        {
            ["manga?title"] = FakeHttpClientFactory.Fixture("mangadex-search.json")
        }));

    [Fact]
    public async Task Search_results_carry_the_tracker_ids_from_the_same_response()
    {
        var results = await WithSearch().SearchAsync("one piece");

        var ids = results[0].ExternalIds;
        Assert.NotNull(ids);
        Assert.Equal("13", ids[ExternalIdService.Mal]);
        Assert.Equal("30013", ids[ExternalIdService.AniList]);
        Assert.Equal("pb8uwds", ids[ExternalIdService.MangaUpdates]);
    }

    [Fact]
    public async Task A_results_own_uuid_is_one_of_its_ids()
    {
        // So a series whose metadata already names a MangaDex title matches on that alone.
        var results = await WithSearch().SearchAsync("one piece");

        Assert.Equal(
            "a1c7c817-4e59-43b7-9365-09675a149a6f",
            results[0].ExternalIds![ExternalIdService.MangaDex]);
    }

    [Fact]
    public async Task Kitsu_slugs_and_store_links_are_not_recorded_as_ids()
    {
        // MangaDex's "kt" is a slug where MangaBaka stores a number, so it can never match - and
        // keeping it would only ever rule the right result out. The store/raw links aren't trackers.
        var ids = (await WithSearch().SearchAsync("one piece"))[0].ExternalIds!;

        Assert.DoesNotContain(ExternalIdService.Kitsu, ids.Keys);
        Assert.Equal(4, ids.Count);
    }

    [Fact]
    public async Task An_entry_with_no_links_still_reports_its_own_uuid()
    {
        var results = await WithSearch().SearchAsync("one piece");

        // Empty object and null are both shapes the API sends.
        Assert.Equal(
            [ExternalIdService.MangaDex],
            results[1].ExternalIds!.Keys);
        Assert.Equal(
            [ExternalIdService.MangaDex],
            results[2].ExternalIds!.Keys);
    }
}
