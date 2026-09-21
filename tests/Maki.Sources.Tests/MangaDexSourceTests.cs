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
    public async Task The_feed_is_asked_for_english_when_the_mapping_names_no_language()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-multilingual.json")
        });

        await new MangaDexSource(factory).ListChaptersAsync("a1c7c817");

        // The default is English, not "every language": an untouched mapping has to keep listing
        // what it listed before, or every existing series grows a chapter row per translation.
        var feed = Assert.Single(factory.Requests);
        Assert.Contains("translatedLanguage[]=en", feed);
        Assert.Equal(1, feed.Split("translatedLanguage[]=").Length - 1);
    }

    [Fact]
    public async Task Several_languages_are_repeated_into_one_request_and_tagged_individually()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-multilingual.json")
        });

        var chapters = await new MangaDexSource(factory).ListChaptersAsync("a1c7c817", "en,es-la");

        // translatedLanguage[] is repeated, so several languages cost one paged walk rather than
        // one per language.
        var feed = Assert.Single(factory.Requests);
        Assert.Contains("translatedLanguage[]=en", feed);
        Assert.Contains("translatedLanguage[]=es-la", feed);

        // Chapter identity is (Number, Language), so chapter 1 survives in both languages instead of
        // one of them winning the dedupe — and each carries the language MangaDex published it in,
        // not the one that happened to be asked for first.
        Assert.Equal(
            [("1", "en"), ("1", "es-la"), ("2", "en")],
            chapters
                .Select(c => (c.NumberRaw!, c.Language))
                .OrderBy(c => c.Item1)
                .ThenBy(c => c.Item2)
                .ToArray());
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
