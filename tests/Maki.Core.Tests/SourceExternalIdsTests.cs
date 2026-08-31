using Maki.Core.Sources;

namespace Maki.Core.Tests;

/// <summary>
/// Covers <see cref="SourceExternalIds"/>: pulling tracker ids out of a series page's outbound links,
/// the id-form rules that keep an uncomparable id from being recorded at all, and the match/mismatch
/// verdict <c>SourceMatchService</c> acts on.
/// </summary>
public class SourceExternalIdsTests
{
    [Fact]
    public void Tracker_links_are_read_off_a_series_page()
    {
        var ids = SourceExternalIds.FromUrls([
            "https://weebcentral.com/series/01J76XY7E9FNDZ1DBBM6PBJPFK",
            "https://myanimelist.net/manga/13/One-Piece",
            "https://anilist.co/manga/30013/ONE-PIECE/",
            "https://www.mangaupdates.com/series/pb8uwds/one-piece",
            "https://mangadex.org/title/a1c7c817-4e59-43b7-9365-09675a149a6f/one-piece",
            "https://kitsu.io/manga/38"
        ]);

        Assert.Equal("13", ids[ExternalIdService.Mal]);
        Assert.Equal("30013", ids[ExternalIdService.AniList]);
        Assert.Equal("pb8uwds", ids[ExternalIdService.MangaUpdates]);
        Assert.Equal("a1c7c817-4e59-43b7-9365-09675a149a6f", ids[ExternalIdService.MangaDex]);
        Assert.Equal("38", ids[ExternalIdService.Kitsu]);
    }

    [Fact]
    public void An_unrelated_link_contributes_nothing()
    {
        var ids = SourceExternalIds.FromUrls(["https://www.viz.com/one-piece", null, "   "]);

        Assert.Empty(ids);
    }

    [Theory]
    // MangaUpdates moved to base36 slugs and MangaBaka stores those, so a legacy numeric id can
    // never match one - recording it would only ever produce a false mismatch.
    [InlineData(ExternalIdService.MangaUpdates, "12345", false)]
    [InlineData(ExternalIdService.MangaUpdates, "pb8uwds", true)]
    // Kitsu addresses a series by numeric id or slug; MangaBaka stores the number.
    [InlineData(ExternalIdService.Kitsu, "one-piece", false)]
    [InlineData(ExternalIdService.Kitsu, "38", true)]
    [InlineData(ExternalIdService.Mal, "13", true)]
    [InlineData(ExternalIdService.Mal, "one-piece", false)]
    [InlineData(ExternalIdService.MangaDex, "a1c7c817-4e59-43b7-9365-09675a149a6f", true)]
    [InlineData(ExternalIdService.MangaDex, "12345", false)]
    public void Only_the_id_forms_MangaBaka_stores_are_comparable(string service, string id, bool expected) =>
        Assert.Equal(expected, SourceExternalIds.IsComparable(service, id));

    [Fact]
    public void An_uncomparable_id_is_dropped_rather_than_recorded()
    {
        var ids = SourceExternalIds.From(
            (ExternalIdService.Mal, "13"),
            (ExternalIdService.Kitsu, "one-piece"),
            (ExternalIdService.AniList, null),
            (ExternalIdService.MangaUpdates, "  pb8uwds  "));

        Assert.Equal(["mal", "mangaupdates"], ids.Keys.OrderBy(k => k));
        Assert.Equal("pb8uwds", ids[ExternalIdService.MangaUpdates]);
    }

    [Fact]
    public void Agreement_on_one_service_is_a_match()
    {
        var verdict = SourceExternalIds.Compare(
            SourceExternalIds.From((ExternalIdService.Mal, "13")),
            SourceExternalIds.From((ExternalIdService.Mal, "13"), (ExternalIdService.AniList, "30013")));

        Assert.Equal(ExternalIdVerdict.Match, verdict);
    }

    [Fact]
    public void One_agreement_outweighs_a_disagreement()
    {
        // A site whose AniList link has gone stale is far more likely than two different works
        // sharing a MyAnimeList id.
        var verdict = SourceExternalIds.Compare(
            SourceExternalIds.From((ExternalIdService.Mal, "13"), (ExternalIdService.AniList, "30013")),
            SourceExternalIds.From((ExternalIdService.Mal, "13"), (ExternalIdService.AniList, "40404")));

        Assert.Equal(ExternalIdVerdict.Match, verdict);
    }

    [Fact]
    public void Every_shared_service_disagreeing_is_a_mismatch()
    {
        var verdict = SourceExternalIds.Compare(
            SourceExternalIds.From((ExternalIdService.Mal, "13")),
            SourceExternalIds.From((ExternalIdService.Mal, "999")));

        Assert.Equal(ExternalIdVerdict.Mismatch, verdict);
    }

    [Fact]
    public void No_shared_service_is_no_evidence()
    {
        // Neither side is wrong here - they simply don't name the same tracker, so the ids can't
        // decide anything and the title pass has to.
        var verdict = SourceExternalIds.Compare(
            SourceExternalIds.From((ExternalIdService.Mal, "13")),
            SourceExternalIds.From((ExternalIdService.AniList, "30013")));

        Assert.Equal(ExternalIdVerdict.NoEvidence, verdict);
    }

    [Fact]
    public void An_absent_map_is_no_evidence()
    {
        var ours = SourceExternalIds.From((ExternalIdService.Mal, "13"));

        Assert.Equal(ExternalIdVerdict.NoEvidence, SourceExternalIds.Compare(ours, null));
        Assert.Equal(ExternalIdVerdict.NoEvidence, SourceExternalIds.Compare(null, ours));
        Assert.Equal(ExternalIdVerdict.NoEvidence, SourceExternalIds.Compare(ours, new Dictionary<string, string>()));
    }

    [Fact]
    public void A_uuid_compares_regardless_of_case()
    {
        var verdict = SourceExternalIds.Compare(
            SourceExternalIds.From((ExternalIdService.MangaDex, "A1C7C817-4E59-43B7-9365-09675A149A6F")),
            SourceExternalIds.From((ExternalIdService.MangaDex, "a1c7c817-4e59-43b7-9365-09675a149a6f")));

        Assert.Equal(ExternalIdVerdict.Match, verdict);
    }

    [Fact]
    public void Merge_keeps_both_halves_of_what_a_source_publishes()
    {
        // Atsumaru's shape: the WeebCentral id rides the search response, the trackers live on the
        // series page. Whichever half confirmed a match, the other is still worth keeping.
        var merged = SourceExternalIds.Merge(
            SourceExternalIds.From((ExternalIdService.WeebCentral, "01BAREULID")),
            SourceExternalIds.From((ExternalIdService.Mal, "13")));

        Assert.Equal("01BAREULID", merged[ExternalIdService.WeebCentral]);
        Assert.Equal("13", merged[ExternalIdService.Mal]);
    }

    [Fact]
    public void Merge_lets_the_second_map_win_a_disagreement()
    {
        var merged = SourceExternalIds.Merge(
            SourceExternalIds.From((ExternalIdService.Mal, "13")),
            SourceExternalIds.From((ExternalIdService.Mal, "999")));

        Assert.Equal("999", merged[ExternalIdService.Mal]);
    }

    [Fact]
    public void Merge_tolerates_an_absent_half()
    {
        var ids = SourceExternalIds.From((ExternalIdService.Mal, "13"));

        Assert.Equal("13", SourceExternalIds.Merge(ids, null)[ExternalIdService.Mal]);
        Assert.Equal("13", SourceExternalIds.Merge(null, ids)[ExternalIdService.Mal]);
        Assert.Empty(SourceExternalIds.Merge(null, null));
    }

    [Fact]
    public void A_service_naming_a_source_is_that_sources_own_series_id()
    {
        // SourceMatchService resolves one of these to a source by name equality, so the key has to
        // stay spelled exactly like the source it addresses.
        Assert.Contains(ExternalIdService.MangaDex, ExternalIdService.SourceSeriesIdServices);
        Assert.Contains(ExternalIdService.WeebCentral, ExternalIdService.SourceSeriesIdServices);
        Assert.DoesNotContain(ExternalIdService.Mal, ExternalIdService.SourceSeriesIdServices);
    }
}
