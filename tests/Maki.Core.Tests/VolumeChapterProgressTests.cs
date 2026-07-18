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

    [Fact]
    public void Advances_to_the_chapter_whose_pages_are_fully_read()
    {
        // Volume 1 = one archive with chapters 1 (pages 0-2), 2 (pages 3-4), 3 (page 5).
        var boundaries = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>
        {
            [1] = Boundaries(6, (1m, 0), (2m, 3), (3m, 5)),
        };

        // 4 of 6 pages read -> chapter 1 (0-2) fully read, chapter 2 (3-4) not yet (needs 5).
        var reached = VolumeChapterProgress.Refine([Volume(1, 6, 4)], boundaries, baseMaxChapter: 0);

        Assert.Equal(1m, reached);
    }

    [Fact]
    public void Reaching_the_next_chapters_start_page_completes_the_previous_chapter()
    {
        var boundaries = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>
        {
            [1] = Boundaries(6, (1m, 0), (2m, 3), (3m, 5)),
        };

        var reached = VolumeChapterProgress.Refine([Volume(1, 6, 5)], boundaries, baseMaxChapter: 0);

        Assert.Equal(2m, reached);
    }

    [Fact]
    public void Reading_every_page_completes_the_last_chapter()
    {
        var boundaries = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>
        {
            [1] = Boundaries(6, (1m, 0), (2m, 3), (3m, 5)),
        };

        var reached = VolumeChapterProgress.Refine([Volume(1, 6, 6)], boundaries, baseMaxChapter: 0);

        Assert.Equal(3m, reached);
    }

    [Fact]
    public void Never_lowers_the_base_chapter()
    {
        var boundaries = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>
        {
            [1] = Boundaries(6, (1m, 0), (2m, 3)),
        };

        // base already ahead (e.g. from another, already chapter-split volume)
        var reached = VolumeChapterProgress.Refine([Volume(1, 6, 3)], boundaries, baseMaxChapter: 10);

        Assert.Equal(10m, reached);
    }

    [Fact]
    public void Ignores_volumes_with_no_matching_boundary_entry()
    {
        var boundaries = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>
        {
            [1] = Boundaries(6, (1m, 0), (2m, 3)),
        };

        // volume 2 has no local archive scanned for it
        var reached = VolumeChapterProgress.Refine([Volume(2, 40, 40)], boundaries, baseMaxChapter: 0);

        Assert.Equal(0m, reached);
    }
}
