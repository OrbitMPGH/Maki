using Maki.Api.Services;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>Which chapter the Discover preview opens on a source's listing.</summary>
public class SeriesPreviewTests
{
    private static SourceChapter Chapter(decimal? number, string id = "") =>
        new("fake", "s", id == "" ? number?.ToString() ?? "x" : id, number?.ToString(), number, null, null, "en", null);

    [Fact]
    public void Picks_chapter_one_even_when_a_prologue_is_listed()
    {
        var picked = SeriesPreviewService.PickFirstChapter([Chapter(3), Chapter(0), Chapter(1), Chapter(2)]);

        Assert.Equal(1m, picked?.Number);
    }

    [Fact]
    public void Falls_back_to_the_lowest_numbered_chapter()
    {
        // MangaDex drops old scans, so a listing can start well past chapter 1.
        var picked = SeriesPreviewService.PickFirstChapter([Chapter(52), Chapter(48), Chapter(50)]);

        Assert.Equal(48m, picked?.Number);
    }

    [Fact]
    public void Unnumbered_extras_are_never_picked()
    {
        Assert.Equal(7m, SeriesPreviewService.PickFirstChapter([Chapter(null, "oneshot"), Chapter(7)])?.Number);
        Assert.Null(SeriesPreviewService.PickFirstChapter([Chapter(null, "oneshot")]));
    }

    [Fact]
    public void An_empty_listing_has_no_first_chapter() =>
        Assert.Null(SeriesPreviewService.PickFirstChapter([]));

    private static SeriesPreviewService.ListedCandidate Listed(string name, decimal? first) =>
        new(new SourceCandidate(new FakeSource { Name = name }, "s", null), first is null ? null : Chapter(first), null);

    [Fact]
    public void A_source_with_chapter_one_beats_a_higher_ranked_one_that_starts_later()
    {
        // Official sites tend to list only the newest free chapters, and a preview of chapter 3 is
        // not what anyone opened it for.
        var order = SeriesPreviewService.FetchOrder(
            [Listed("webtoons", 3), Listed("empty", null), Listed("mangadex", 1), Listed("weebcentral", 1)]);

        Assert.Equal(["mangadex", "weebcentral", "webtoons"], order.Select(l => l.Candidate.Source.Name));
    }
}
