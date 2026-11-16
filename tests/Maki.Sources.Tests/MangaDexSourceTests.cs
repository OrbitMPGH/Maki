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
    public async Task GetChapterVolumes_drops_a_number_whose_feed_entries_disagree_on_volume()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-conflicting-volumes.json")
        });

        var volumes = await new MangaDexSource(factory).GetChapterVolumesAsync("a1c7c817");

        // Chapter 1 disagrees (vol 1 vs vol 2 across languages) and must not be stamped with
        // whichever volume happened to be seen first.
        Assert.False(volumes.ContainsKey(1m));
        // Chapter 2 is consistent across both entries and is kept.
        Assert.Equal(1, volumes[2m]);
    }

    [Fact]
    public async Task GetChapterVolumes_walks_the_english_feed_first()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-conflicting-volumes.json")
        });

        var volumes = await new MangaDexSource(factory).GetChapterVolumesAsync("a1c7c817");

        Assert.Equal(1, volumes[2m]);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Contains("includeUnavailable=1", factory.Requests[0]);
        Assert.Contains("translatedLanguage[]=en", factory.Requests[0]);
        Assert.DoesNotContain("translatedLanguage", factory.Requests[1]);
    }

    [Fact]
    public async Task GetChapterVolumes_fills_the_numbers_english_leaves_out_from_every_language()
    {
        // English knows chapter 7 only; the all-language feed also assigns chapter 9, and disagrees
        // with English about chapter 7, where English wins.
        var factory = new FakeHttpClientFactory(new()
        {
            ["translatedLanguage[]=en"] = FeedPage(total: 1, volume: "1"),
            ["feed"] = FeedPage(total: 1, volume: "2").Replace("\"chapter\":\"7\"", "\"chapter\":\"9\"")
        });

        var volumes = await new MangaDexSource(factory).GetChapterVolumesAsync("a1c7c817");

        Assert.Equal(1, volumes[7m]);
        Assert.Equal(2, volumes[9m]);
    }

    [Fact]
    public async Task GetChapterVolumes_skips_a_fill_that_breaks_the_english_volume_order()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["translatedLanguage[]=en"] = Feed(("1", "1", "en"), ("10", "2", "en"), ("20", "4", "en")),
            ["feed"] = Feed(("1", "1", "en"), ("10", "2", "en"), ("20", "4", "en"),
                ("5", "1", "es"), ("15", "1", "fr"), ("25", "3", "fr"), ("30", "5", "es"))
        });

        var volumes = await new MangaDexSource(factory).GetChapterVolumesAsync("a1c7c817");

        Assert.Equal(1, volumes[5m]);
        Assert.Equal(5, volumes[30m]);
        // Chapter 15 cannot be in volume 1 after chapter 10 is in volume 2, and chapter 25 cannot be
        // in volume 3 after chapter 20 is in volume 4: an edition numbered differently.
        Assert.False(volumes.ContainsKey(15m));
        Assert.False(volumes.ContainsKey(25m));
    }

    [Fact]
    public async Task GetChapterVolumes_fills_nothing_where_the_other_languages_disagree()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["translatedLanguage[]=en"] = Feed(("1", "1", "en")),
            ["feed"] = Feed(("1", "1", "en"), ("12", "3", "es"), ("12", "4", "fr"), ("13", "3", "es"))
        });

        var volumes = await new MangaDexSource(factory).GetChapterVolumesAsync("a1c7c817");

        Assert.False(volumes.ContainsKey(12m));
        Assert.Equal(3, volumes[13m]);
    }

    private static string Feed(params (string Chapter, string Volume, string Language)[] rows)
    {
        var data = string.Join(",", rows.Select((r, i) =>
            $"{{\"id\":\"00000000-0000-0000-0000-{i:000000000000}\",\"type\":\"chapter\",\"attributes\":{{" +
            $"\"chapter\":\"{r.Chapter}\",\"volume\":\"{r.Volume}\",\"title\":null,\"translatedLanguage\":\"{r.Language}\"," +
            "\"externalUrl\":null,\"isUnavailable\":false,\"pages\":10,\"publishAt\":\"2018-01-01T00:00:00+00:00\"}}"));
        return $"{{\"result\":\"ok\",\"response\":\"collection\",\"limit\":500,\"offset\":0,\"total\":{rows.Length},\"data\":[{data}]}}";
    }

    [Fact]
    public async Task GetChapterVolumes_falls_back_to_every_language_when_english_has_no_volumes()
    {
        var empty = FeedPage(total: 0, volume: null);
        var factory = new FakeHttpClientFactory(new()
        {
            ["translatedLanguage[]=en"] = empty,
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-conflicting-volumes.json")
        });

        var volumes = await new MangaDexSource(factory).GetChapterVolumesAsync("a1c7c817");

        Assert.Equal(1, volumes[2m]);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Contains("translatedLanguage[]=en", factory.Requests[0]);
        Assert.DoesNotContain("translatedLanguage", factory.Requests[1]);
    }

    [Fact]
    public async Task GetChapterVolumes_stops_before_the_offset_cap_and_keeps_what_it_read()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FeedPage(total: 40000, volume: "3")
        });

        var volumes = await new MangaDexSource(factory).GetChapterVolumesAsync("a1c7c817");

        Assert.Equal(3, volumes[7m]);
        // The English walk and the all-language walk each stop at the cap.
        Assert.Equal(40, factory.Requests.Count);
        Assert.DoesNotContain(factory.Requests, r => r.Contains("offset=10000", StringComparison.Ordinal));
    }

    private static string FeedPage(int total, string? volume)
    {
        var chapter = volume is null
            ? string.Empty
            : "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"type\":\"chapter\",\"attributes\":{" +
              $"\"chapter\":\"7\",\"volume\":\"{volume}\",\"title\":null,\"translatedLanguage\":\"en\"," +
              "\"externalUrl\":null,\"isUnavailable\":false,\"pages\":10,\"publishAt\":\"2018-01-01T00:00:00+00:00\"}}";
        return $"{{\"result\":\"ok\",\"response\":\"collection\",\"limit\":500,\"offset\":0,\"total\":{total},\"data\":[{chapter}]}}";
    }

    [Fact]
    public async Task Chapters_tagged_zh_are_listed_under_the_canonical_simplified_chinese_tag()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FeedPage(total: 1, volume: "1").Replace("\"en\"", "\"zh\"")
        });

        var chapters = await new MangaDexSource(factory).ListChaptersAsync("a1c7c817", "zh-Hans");

        Assert.Equal("zh-Hans", Assert.Single(chapters).Language);
    }

    [Fact]
    public async Task A_simplified_chinese_filter_is_requested_as_plain_zh()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-multilingual.json")
        });

        await new MangaDexSource(factory).ListChaptersAsync("a1c7c817", "zh-Hans");

        var feed = Assert.Single(factory.Requests);
        Assert.Contains("translatedLanguage[]=zh&", feed + "&");
        Assert.DoesNotContain("zh-hans", feed);
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
    public async Task Feed_request_includes_scanlation_group_relationships()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-multilingual.json")
        });

        await new MangaDexSource(factory).ListChaptersAsync("a1c7c817");

        var feed = Assert.Single(factory.Requests);
        Assert.Contains("includes[]=scanlation_group", feed);
    }

    [Fact]
    public async Task Chapters_carry_the_uploading_scanlation_groups()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["feed"] = FakeHttpClientFactory.Fixture("mangadex-feed-multilingual.json")
        });

        var chapters = await new MangaDexSource(factory).ListChaptersAsync("a1c7c817", "en,es-la");

        // Chapter 1/en was kept from a single group; chapter 1/es-la is a joint release of two,
        // joined with ", "; chapter 2/en carries no scanlation_group relationship at all.
        Assert.Equal("Scanlation Corp", chapters.Single(c => c.NumberRaw == "1" && c.Language == "en").Group);
        Assert.Equal("Grupo A, Grupo B", chapters.Single(c => c.Language == "es-la").Group);
        Assert.Null(chapters.Single(c => c.NumberRaw == "2").Group);
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
