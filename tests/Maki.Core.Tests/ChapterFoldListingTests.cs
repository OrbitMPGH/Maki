using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class ChapterFoldListingTests
{
    private static SourceChapter C(decimal number, string? id = null, int? pages = null) =>
        new("parts", "series", id ?? FormattableString.Invariant($"id-{number}"), number.ToString(System.Globalization.CultureInfo.InvariantCulture), number,
            null, $"Part of {number}", "en", new DateTime(2026, 1, 1).AddDays((double)number), PageCount: pages);

    private static ChapterFold Fold(decimal floor, params decimal[] parts) => new()
    {
        SourceMappingId = 1,
        Language = "en",
        Floor = floor,
        PartsJson = ChapterFoldPart.Format(parts.Select(p => new ChapterFoldPart(FormattableString.Invariant($"id-{p}"), p, null, 10))),
    };

    [Fact]
    public void ListedPartsBecomeTheirChapter()
    {
        var result = ChapterFoldListing.Rewrite([C(2), C(3.1m, pages: 13), C(3.2m, pages: 26), C(4)], [Fold(3, 3.1m, 3.2m)], complete: true);

        Assert.Equal([2m, 3m, 4m], result.Chapters.Select(c => c.Number!.Value));
        var three = result.Chapters[1];
        Assert.Equal("id-3.1", three.SourceChapterId);
        Assert.Equal("3", three.NumberRaw);
        Assert.Equal(39, three.PageCount);
        Assert.Null(three.Title);
        Assert.True(result.Parts.ContainsKey("id-3.1"));
        Assert.Empty(result.Broken);
    }

    [Fact]
    public void AMissingPartBreaksTheFold()
    {
        var fold = Fold(3, 3.1m, 3.2m);

        var result = ChapterFoldListing.Rewrite([C(3.1m), C(4)], [fold], complete: true);

        Assert.Equal([3.1m, 4m], result.Chapters.Select(c => c.Number!.Value));
        Assert.Equal([fold], result.Broken);
    }

    [Fact]
    public void AnotherUploadUnderThePartsNumberBreaksTheFold()
    {
        var result = ChapterFoldListing.Rewrite([C(3.1m), C(3.2m, id: "reupload")], [Fold(3, 3.1m, 3.2m)], complete: true);

        Assert.Single(result.Broken);
    }

    [Fact]
    public void APartialListingNeverBreaksAFold()
    {
        var result = ChapterFoldListing.Rewrite([C(3.2m), C(4)], [Fold(3, 3.1m, 3.2m)], complete: false);

        Assert.Equal([4m], result.Chapters.Select(c => c.Number!.Value));
        Assert.Empty(result.Broken);
    }

    [Fact]
    public void APartialListingLeavesOutAPartUploadedAgain()
    {
        var result = ChapterFoldListing.Rewrite([C(3.1m), C(3.2m, id: "reupload"), C(4)], [Fold(3, 3.1m, 3.2m)], complete: false);

        Assert.Equal([4m], result.Chapters.Select(c => c.Number!.Value));
        Assert.Empty(result.Broken);
    }

    [Fact]
    public void PartsBesideTheSourcesOwnWholeAreOnlyLeftOut()
    {
        var result = ChapterFoldListing.Rewrite([C(3), C(3.1m), C(3.2m)], [Fold(3, 3.1m, 3.2m)], complete: true);

        Assert.Equal([3m], result.Chapters.Select(c => c.Number!.Value));
        Assert.Equal("id-3", result.Chapters[0].SourceChapterId);
        Assert.Empty(result.Parts);
    }
}
