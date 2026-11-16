using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Parsing;

namespace Maki.Api.Tests;

/// <summary>What the import preview says a volume file would back, which has to match the linker.</summary>
public class TorrentImportCoverageTests
{
    private static List<Chapter> VolumeOne(params decimal[] numbers) =>
        numbers.Select((n, i) => new Chapter { Id = i + 1, Number = n, Volume = 1, Language = "en" }).ToList();

    private static string[] Pages(params int[] chapters) =>
        chapters.Select(c => $"Berserk - c{c:000} (v01) - p001 [Oak].png").ToArray();

    [Fact]
    public void Splits_the_page_markers_leave_out_are_not_covered()
    {
        var chapters = VolumeOne(1, 2, 2.1m, 2.2m, 3);

        var covered = TorrentImportService.ChaptersCoveredBy(
            chapters, ReleaseNameParser.ParseFileName("Berserk v01 (Digital) (Oak).cbz"), Pages(1, 2, 3));

        Assert.Equal([1m, 2m, 3m], covered.Select(c => c.Number!.Value));
    }

    [Fact]
    public void Markers_in_another_numbering_leave_the_range_in_charge()
    {
        var chapters = VolumeOne(7, 8, 9);

        var covered = TorrentImportService.ChaptersCoveredBy(
            chapters, ReleaseNameParser.ParseFileName("Berserk v01 (Digital) (Oak).cbz"), Pages(1, 2, 3));

        Assert.Equal([7m, 8m, 9m], covered.Select(c => c.Number!.Value));
    }
}
