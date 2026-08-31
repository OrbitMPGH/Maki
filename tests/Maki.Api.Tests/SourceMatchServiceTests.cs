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

    private Task<List<string>> RunAutoMatch(int seriesId, params ISource[] sources) =>
        RunAutoMatch(seriesId, Sources.AllEnabled, sources);

    private async Task<List<string>> RunAutoMatch(
        int seriesId, SourceAvailability availability, params ISource[] sources)
    {
        var context = _db.NewContext();
        var series = await context.Series.Include(s => s.SourceMappings).FirstAsync(s => s.Id == seriesId);
        var service = new SourceMatchService(
            context, new SourceRegistry(sources), new FakeAppSettings(), availability,
            new SourceExternalIdCache(TimeProvider.System), NullLogger<SourceMatchService>.Instance);
        return await service.AutoMatchAsync(series);
    }

    private static SourceSeriesResult Hit(string title) =>
        new(SourceSeriesId: "sid", Title: title, Url: "https://x.test/s");

    private static SourceSeriesResult Hit(string id, string title) =>
        new(SourceSeriesId: id, Title: title, Url: $"https://x.test/{id}");

    /// <summary>A hit whose tracker ids arrived with the search response, as MangaDex's do.</summary>
    private static SourceSeriesResult HitWithIds(string id, string title, params (string, string?)[] ids) =>
        new(SourceSeriesId: id, Title: title, Url: $"https://x.test/{id}",
            ExternalIds: SourceExternalIds.From(ids));

    private static Action<Series> WithIds(int? mal = null, int? aniList = null) =>
        series =>
        {
            series.MalId = mal;
            series.AniListId = aniList;
        };

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
    public async Task Globally_disabled_source_is_not_auto_mapped()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var off = new FakeSource { Name = "off", OnSearch = _ => [Hit("Hajime no Ippo")] };
        var on = new FakeSource { Name = "on", OnSearch = _ => [Hit("Hajime no Ippo")] };

        var mapped = await RunAutoMatch(seriesId, Sources.Disabled("off"), off, on);

        Assert.Equal(["on"], mapped);
        Assert.Equal(0, off.SearchCalls);
    }

    [Fact]
    public async Task Disabling_a_source_does_not_renumber_the_priorities_around_it()
    {
        // Priority is the position in the full ordered list, so a mapping's number is the same
        // whether or not a higher-ranked source happens to be switched off — which is what keeps
        // it in agreement with SourceMappingController's own priority calculation.
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var first = new FakeSource { Name = "first", OnSearch = _ => [Hit("Hajime no Ippo")] };
        var second = new FakeSource { Name = "second", OnSearch = _ => [Hit("Hajime no Ippo")] };

        await RunAutoMatch(seriesId, Sources.Disabled("first"), first, second);

        Assert.Equal(2, Assert.Single(MappingsOf(seriesId)).Priority);
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

    [Fact]
    public async Task Cross_id_match_accepts_a_result_whose_title_would_never_score()
    {
        // The site's entry is titled in romaji while the library holds the English name; fuzzy
        // matching cannot bridge that, but both sides name the same MyAnimeList entry.
        var seriesId = _db.SeedSeries("Attack on Titan", configure: WithIds(mal: 23390));
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Shingeki no Kyojin")],
            OnExternalIds = _ => SourceExternalIds.From((ExternalIdService.Mal, "23390"))
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
        Assert.Equal("a", Assert.Single(MappingsOf(seriesId)).SourceSeriesId);
    }

    [Fact]
    public async Task Cross_id_mismatch_drops_a_result_the_title_pass_would_have_taken()
    {
        // Two works with the same title. Without the id check the first (wrong) hit wins on an exact
        // title score and nothing about the mapping ever looks wrong.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("wrong", "Hajime no Ippo"), Hit("right", "Hajime no Ippo")],
            OnExternalIds = id => SourceExternalIds.From(
                (ExternalIdService.Mal, id == "right" ? "13" : "999"))
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
        Assert.Equal("right", Assert.Single(MappingsOf(seriesId)).SourceSeriesId);
    }

    [Fact]
    public async Task A_single_agreeing_service_wins_over_one_that_disagrees()
    {
        // Scraped ids go stale one tracker at a time; two different works sharing a tracker id do not
        // happen. One agreement is therefore enough, even alongside a disagreement.
        var seriesId = _db.SeedSeries("Berserk", configure: WithIds(mal: 2, aniList: 30002));
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Something Else Entirely")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "2"), (ExternalIdService.AniList, "40404"))
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
    }

    [Fact]
    public async Task Search_carried_ids_are_used_without_any_lookup()
    {
        var seriesId = _db.SeedSeries("One Piece", configure: WithIds(mal: 13));
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [HitWithIds("a", "Wan Pisu", (ExternalIdService.Mal, "13"))],
            OnExternalIds = _ => throw new InvalidOperationException("must not be called")
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
        Assert.Equal(0, source.ExternalIdCalls);
    }

    [Fact]
    public async Task A_series_with_no_cross_ids_costs_no_lookups()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From((ExternalIdService.Mal, "13"))
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
        Assert.Equal(0, source.ExternalIdCalls);
    }

    [Fact]
    public async Task Lookups_are_capped_and_go_to_the_closest_titles_first()
    {
        // Each lookup is a page fetch through the source's shared rate limiter, so a twenty-hit
        // search must not become twenty scrapes.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var looked = new List<string>();
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ =>
            [
                Hit("far1", "Completely Unrelated One"),
                Hit("far2", "Completely Unrelated Two"),
                Hit("far3", "Completely Unrelated Three"),
                Hit("near", "Hajime no Ippo: Fighting Spirit!"),
                Hit("exact", "Hajime no Ippo")
            ],
            OnExternalIds = id =>
            {
                looked.Add(id);
                return null;
            }
        };

        await RunAutoMatch(seriesId, source);

        Assert.Equal(3, source.ExternalIdCalls);
        Assert.Equal(["exact", "near"], looked.Take(2));
    }

    [Fact]
    public async Task A_failed_lookup_still_leaves_the_title_pass_to_match()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => throw new HttpRequestException("site down")
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
        Assert.Equal("a", Assert.Single(MappingsOf(seriesId)).SourceSeriesId);
    }

    [Fact]
    public async Task A_source_publishing_no_ids_falls_through_to_the_title_pass()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var source = new FakeSource { Name = "fake", OnSearch = _ => [Hit("a", "Hajime no Ippo")] };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Equal(["fake"], mapped);
        Assert.Equal(1, source.ExternalIdCalls);
    }

    [Fact]
    public async Task Every_result_ruled_out_by_id_leaves_the_series_unmapped()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From((ExternalIdService.Mal, "999"))
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Empty(mapped);
        Assert.Empty(MappingsOf(seriesId));
    }

    /// <summary>A source named after a cross-reference service, standing in for the real MangaDex.</summary>
    private static FakeSource CrossRefTarget(
        string name = "mangadex", Func<string, IReadOnlyList<SourceSeriesResult>>? onSearch = null) =>
        new()
        {
            Name = name,
            OnSearch = onSearch ?? (_ => []),
            OnGetSeries = id => new SourceSeriesDetail(id, "Whatever The Site Calls It", $"https://{name}.test/{id}")
        };

    private const string DexUuid = "a1c7c817-4e59-43b7-9365-09675a149a6f";

    [Fact]
    public async Task A_confirmed_match_maps_a_source_whose_own_search_found_nothing()
    {
        // The confirming source names the MangaDex title outright, so a source that came back with
        // no usable result of its own still gets mapped - and no title is involved, so the twin a
        // fuzzy match would have picked cannot be picked here.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "13"), (ExternalIdService.MangaDex, DexUuid))
        };
        var target = CrossRefTarget();

        var mapped = await RunAutoMatch(seriesId, confirming, target);

        Assert.Equal(["fake", "mangadex"], mapped);
        var seeded = Assert.Single(MappingsOf(seriesId), m => m.SourceName == "mangadex");
        Assert.Equal(DexUuid, seeded.SourceSeriesId);
        Assert.Equal($"https://mangadex.test/{DexUuid}", seeded.Url);
    }

    [Fact]
    public async Task Ids_from_a_title_only_match_are_not_spent_on_another_source()
    {
        // The match here is a guess about a title, so its cross-references are guesses too. Seeding
        // off one would turn a single wrong guess into two wrong mappings.
        var seriesId = _db.SeedSeries("Hajime no Ippo");
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [new SourceSeriesResult("a", "Hajime no Ippo", "https://fake.test/a",
                ExternalIds: SourceExternalIds.From((ExternalIdService.MangaDex, DexUuid)))]
        };
        var target = CrossRefTarget();

        var mapped = await RunAutoMatch(seriesId, confirming, target);

        Assert.Equal(["fake"], mapped);
        Assert.Equal(0, target.GetSeriesCalls);
    }

    [Fact]
    public async Task A_source_that_found_its_own_entry_keeps_it()
    {
        // The source's own result is canonical; the borrowed id may be a less complete form of it.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "13"), (ExternalIdService.WeebCentral, "01BAREULID"))
        };
        var target = CrossRefTarget("weebcentral", _ => [Hit("01BAREULID/Hajime-no-Ippo", "Hajime no Ippo")]);

        var mapped = await RunAutoMatch(seriesId, confirming, target);

        Assert.Equal(["fake", "weebcentral"], mapped);
        Assert.Equal(
            "01BAREULID/Hajime-no-Ippo",
            Assert.Single(MappingsOf(seriesId), m => m.SourceName == "weebcentral").SourceSeriesId);
        Assert.Equal(0, target.GetSeriesCalls);
    }

    [Fact]
    public async Task A_cross_reference_that_no_longer_resolves_is_dropped()
    {
        // Sites delete entries. Left unchecked the mapping would only fail later, during a sync.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "13"), (ExternalIdService.MangaDex, DexUuid))
        };
        var target = new FakeSource
        {
            Name = "mangadex",
            OnSearch = _ => [],
            OnGetSeries = _ => throw new HttpRequestException("404")
        };

        var mapped = await RunAutoMatch(seriesId, confirming, target);

        Assert.Equal(["fake"], mapped);
        Assert.DoesNotContain(MappingsOf(seriesId), m => m.SourceName == "mangadex");
    }

    [Fact]
    public async Task A_globally_disabled_source_is_not_seeded_either()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "13"), (ExternalIdService.MangaDex, DexUuid))
        };
        var target = CrossRefTarget();

        var mapped = await RunAutoMatch(seriesId, Sources.Disabled("mangadex"), confirming, target);

        Assert.Equal(["fake"], mapped);
        Assert.Equal(0, target.GetSeriesCalls);
    }

    [Fact]
    public async Task A_seeded_mapping_takes_its_place_in_the_priority_order()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "13"), (ExternalIdService.MangaDex, DexUuid))
        };

        await RunAutoMatch(seriesId, confirming, CrossRefTarget());

        Assert.Equal(2, Assert.Single(MappingsOf(seriesId), m => m.SourceName == "mangadex").Priority);
    }

    [Fact]
    public async Task Cross_references_from_search_survive_a_match_confirmed_by_lookup()
    {
        // Atsumaru's shape: the WeebCentral id rides the search response, the trackers that confirm
        // the match live on the series page. Keeping only the half that confirmed loses the other.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [new SourceSeriesResult("a", "Something Else", "https://fake.test/a",
                ExternalIds: SourceExternalIds.From((ExternalIdService.WeebCentral, "01BAREULID")))],
            OnExternalIds = _ => SourceExternalIds.From((ExternalIdService.Mal, "13"))
        };
        var target = CrossRefTarget("weebcentral");

        var mapped = await RunAutoMatch(seriesId, confirming, target);

        Assert.Equal(["fake", "weebcentral"], mapped);
        Assert.Equal(
            "01BAREULID",
            Assert.Single(MappingsOf(seriesId), m => m.SourceName == "weebcentral").SourceSeriesId);
    }

    [Fact]
    public async Task An_id_for_a_service_that_is_not_a_source_seeds_nothing()
    {
        // MyAnimeList is a tracker, not somewhere chapters come from.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var confirming = new FakeSource
        {
            Name = "fake",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From((ExternalIdService.Mal, "13"))
        };
        var mal = CrossRefTarget("mal");

        var mapped = await RunAutoMatch(seriesId, confirming, mal);

        Assert.Equal(["fake"], mapped);
        Assert.Equal(0, mal.GetSeriesCalls);
    }

    [Fact]
    public async Task The_highest_ranked_source_wins_a_disagreement_about_a_cross_reference()
    {
        // Two confirmed matches can still name different MangaDex titles - one of the sites has a
        // stale link. Sources are walked in priority order, so the first to name one is the one the
        // user ranked highest, and it keeps the claim.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: WithIds(mal: 13));
        var first = new FakeSource
        {
            Name = "first",
            OnSearch = _ => [Hit("a", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "13"), (ExternalIdService.MangaDex, DexUuid))
        };
        var second = new FakeSource
        {
            Name = "second",
            OnSearch = _ => [Hit("b", "Hajime no Ippo")],
            OnExternalIds = _ => SourceExternalIds.From(
                (ExternalIdService.Mal, "13"),
                (ExternalIdService.MangaDex, "ffffffff-0000-0000-0000-000000000000"))
        };

        await RunAutoMatch(seriesId, first, second, CrossRefTarget());

        Assert.Equal(
            DexUuid,
            Assert.Single(MappingsOf(seriesId), m => m.SourceName == "mangadex").SourceSeriesId);
    }

    [Fact]
    public async Task A_short_title_is_not_mapped_to_a_longer_one_that_merely_contains_it()
    {
        // Reported case. Both hits clear the 0.6 threshold (0.65 and 0.71) and both cover every word
        // of the query, yet neither is the series - the query is just a fragment of each. Leaving it
        // unmapped puts it in front of the user, which a wrong mapping never does.
        var seriesId = _db.SeedSeries("High School Boy");
        var source = new FakeSource
        {
            Name = "fake",
            OnSearch = _ =>
            [
                Hit("a", "She's Adopted a High School Boy!"),
                Hit("b", "Magic, High School, and a Boy")
            ]
        };

        var mapped = await RunAutoMatch(seriesId, source);

        Assert.Empty(mapped);
        Assert.Empty(MappingsOf(seriesId));
    }
}
