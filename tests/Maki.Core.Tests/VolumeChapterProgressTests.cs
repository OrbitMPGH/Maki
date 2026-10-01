using Maki.Core.Kavita;
using Maki.Core.Scrobbling;

namespace Maki.Core.Tests;

public class VolumeChapterProgressTests
{
    private static KavitaProgress.KavitaVolumeDto Volume(double? number, int pages, int pagesRead) =>
        new(number, number, pages, pagesRead, null);

    private static VolumeChapterProgress.ChapterFileBoundaries Boundaries(
        int totalPages, params (decimal Chapter, int PageIndex)[] boundaries) =>
        new(totalPages, boundaries);

    private static readonly Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries> VolumeOne = new()
    {
        // Volume 1 = one archive with chapters 1 (pages 0-2), 2 (pages 3-4), 3 (page 5).
        [1] = Boundaries(6, (1m, 0), (2m, 3), (3m, 5)),
    };

    [Fact]
    public void Advances_to_the_chapter_whose_pages_are_fully_read()
    {
        // 4 of 6 pages read -> chapter 1 (0-2) fully read, chapter 2 (3-4) not yet (needs 5).
        var progress = KavitaProgress.Compute([Volume(1, 6, 4)], VolumeOne);

        Assert.Equal(1, progress.MaxChapter);
        Assert.Equal([(1m, 1m)], progress.Chapters);
        Assert.Empty(progress.Volumes);
    }

    [Fact]
    public void Reaching_the_next_chapters_start_page_completes_the_previous_chapter()
    {
        var progress = KavitaProgress.Compute([Volume(1, 6, 5)], VolumeOne);

        Assert.Equal(2, progress.MaxChapter);
        Assert.Equal([(1m, 2m)], progress.Chapters);
    }

    [Fact]
    public void Reading_every_page_completes_the_last_chapter_and_the_volume()
    {
        var progress = KavitaProgress.Compute([Volume(1, 6, 6)], VolumeOne);

        Assert.Equal(3, progress.MaxChapter);
        Assert.Equal([(1m, 3m)], progress.Chapters);
        Assert.Equal([(1, 1)], progress.Volumes);
    }

    [Fact]
    public void A_numbered_chapter_further_on_still_wins()
    {
        // Chapter 10 sits in an already chapter-split volume; the half-read archive must not lower it.
        var progress = KavitaProgress.Compute(
        [
            Volume(1, 6, 3),
            new KavitaProgress.KavitaVolumeDto(2, 2, 20, 20,
                [new KavitaProgress.KavitaChapterDto(10, 10, 20, 20, false)]),
        ], VolumeOne);

        Assert.Equal(10, progress.MaxChapter);
    }

    [Fact]
    public void Ignores_volumes_with_no_matching_boundary_entry()
    {
        // volume 2 has no local archive scanned for it
        var progress = KavitaProgress.Compute([Volume(2, 40, 39)], VolumeOne);

        Assert.Equal(0, progress.MaxChapter);
        Assert.True(progress.IsEmpty);
    }
}
