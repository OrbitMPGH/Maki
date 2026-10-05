using System.Text.Json;
using Maki.Core.Kavita;

namespace Maki.Core.Tests;

public class KavitaProgressTests
{
    private static KavitaProgress.KavitaChapterDto Chapter(
        double? number, int pages, int pagesRead, bool isSpecial = false) =>
        new(number, number, pages, pagesRead, isSpecial);

    private static KavitaProgress.KavitaVolumeDto Volume(
        double? number, int pages, int pagesRead, params KavitaProgress.KavitaChapterDto[] chapters) =>
        new(number, number, pages, pagesRead, chapters.ToList());

    [Fact]
    public void HighestFullyReadChapterWins()
    {
        var progress = KavitaProgress.Compute([
            Volume(1, 60, 0,
                Chapter(1, 20, 20),
                Chapter(2, 20, 20),
                Chapter(3, 20, 5)), // partially read — doesn't count
        ]);

        Assert.Equal(2, progress.MaxChapter);
        Assert.Equal(45, progress.ReadPages);
    }

    [Fact]
    public void SpecialsAndSentinelsAreIgnored()
    {
        var progress = KavitaProgress.Compute([
            Volume(1, 60, 0,
                Chapter(1, 20, 20),
                Chapter(100000, 20, 20),          // Kavita sentinel number
                Chapter(7.5, 20, 20, isSpecial: true)),
        ]);

        Assert.Equal(1, progress.MaxChapter);
    }

    [Fact]
    public void FullyReadVolumeAdvancesVolumeCounter()
    {
        var progress = KavitaProgress.Compute([
            Volume(1, 40, 0, Chapter(1, 20, 20), Chapter(2, 20, 20)),
            Volume(2, 40, 0, Chapter(3, 20, 20), Chapter(4, 20, 0)),
        ]);

        Assert.Equal(1, progress.MaxVolume);
        Assert.Equal(3, progress.MaxChapter);
    }

    [Fact]
    public void VolumeOnlyReleasesStillAdvanceVolume()
    {
        // chapters carry no usable number (0 = Kavita's "volume chapter"), but the
        // volume's pages are fully read
        var progress = KavitaProgress.Compute([
            Volume(3, 180, 0, Chapter(0, 180, 180)),
        ]);

        Assert.Equal(0, progress.MaxChapter);
        Assert.Equal(3, progress.MaxVolume);
    }

    [Fact]
    public void VolumeWithoutChaptersUsesOwnPagesRead()
    {
        var progress = KavitaProgress.Compute([
            new KavitaProgress.KavitaVolumeDto(2, 2, 100, 100, null),
        ]);

        Assert.Equal(2, progress.MaxVolume);
        Assert.Equal(100, progress.ReadPages);
    }

    [Fact]
    public void NothingReadYieldsZero()
    {
        var progress = KavitaProgress.Compute([
            Volume(1, 40, 0, Chapter(1, 20, 0), Chapter(2, 20, 0)),
        ]);

        Assert.Equal(0, progress.MaxChapter);
        Assert.Equal(0, progress.MaxVolume);
        Assert.Equal(0, progress.ReadPages);
    }

    [Fact]
    public void DeserializesKavitaStringChapterNumbers()
    {
        // Kavita sends chapter numbers as strings and volume numbers as numbers
        const string json = """
            [{"number": 1, "maxNumber": 1, "pages": 20, "pagesRead": 20,
              "chapters": [
                {"number": "1", "maxNumber": 1.0, "pages": 10, "pagesRead": 10, "isSpecial": false},
                {"number": "1.5", "maxNumber": 1.5, "pages": 10, "pagesRead": 10, "isSpecial": false},
                {"number": "", "maxNumber": null, "pages": 5, "pagesRead": 0, "isSpecial": false}
              ]}]
            """;

        var volumes = JsonSerializer.Deserialize<List<KavitaProgress.KavitaVolumeDto>>(json)!;
        var progress = KavitaProgress.Compute(volumes);

        Assert.Equal(1.5, progress.MaxChapter);
        Assert.Equal(20, progress.ReadPages);
    }

    [Fact]
    public void A_fully_read_chapter_range_covers_every_number_in_it()
    {
        var progress = KavitaProgress.Compute([
            Volume(1, 40, 0,
                new KavitaProgress.KavitaChapterDto(1, 5, 20, 20, false),
                Chapter(6, 20, 19)),
        ]);

        Assert.Equal([(1m, 5m)], progress.Chapters);
        Assert.True(progress.CoversChapter(3.5m));
        Assert.False(progress.CoversChapter(6m));
        Assert.Equal(5, progress.MaxChapter);
    }

    [Fact]
    public void A_volume_archive_reads_as_a_volume_not_a_chapter()
    {
        // How Kavita reports a "Vol. 03.cbz" with no chapter number: one loose-leaf chapter
        // numbered -100000 inside volume 3.
        var progress = KavitaProgress.Compute([
            Volume(3, 180, 0, Chapter(-100000, 180, 180)),
            new KavitaProgress.KavitaVolumeDto(1, 2, 300, 300, null),
        ]);

        Assert.Empty(progress.Chapters);
        Assert.Equal([(3, 3), (1, 2)], progress.Volumes);
    }
}
