using System.IO.Compression;
using Maki.Core.Parsing;

namespace Maki.Core.Tests;

public class VolumeChapterScannerTests
{
    [Fact]
    public void Scans_a_real_compilation_archive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maki-vol-{Guid.NewGuid():N}.cbz");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry("ComicInfo.xml"); // must be skipped (not an image)
                foreach (var page in new[]
                {
                    "Boyish Girlfriend - c049 (v05) - p001 [web] [Manga UP!] [Oak].png",
                    "Boyish Girlfriend - c049 (v05) - p002 [web] [Manga UP!] [Oak].png",
                    "Boyish Girlfriend - c050 (v05) - p001 [web] [Manga UP!] [Oak].png"
                })
                {
                    archive.CreateEntry(page, CompressionLevel.NoCompression);
                }
            }

            Assert.Equal([49m, 50m], VolumeChapterScanner.ScanCbz(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Extracts_chapter_from_real_compilation_page_names()
    {
        // Real "Boyish Girlfriend v05" compilation page-naming scheme.
        string[] names =
        [
            "Boyish Girlfriend - c049 (v05) - p001 [web] [Manga UP!] [Oak].png",
            "Boyish Girlfriend - c049 (v05) - p113 [web] [Manga UP!] [Oak].png",
            "Boyish Girlfriend - c050 (v05) - p001 [web] [Manga UP!] [Oak].png",
            "Boyish Girlfriend - c051 (v05) - p001 [web] [Manga UP!] [Oak].png"
        ];

        Assert.Equal([49m, 50m, 51m], VolumeChapterScanner.ChaptersInNames(names));
    }

    [Fact]
    public void Deduplicates_and_sorts()
    {
        string[] names = ["x - c010 - p002.jpg", "x - c009 - p001.jpg", "x - c010 - p001.jpg"];
        Assert.Equal([9m, 10m], VolumeChapterScanner.ChaptersInNames(names));
    }

    [Fact]
    public void Handles_decimal_chapters()
    {
        string[] names = ["Title - c012.5 (v02) - p001.png", "Title - c013 (v02) - p001.png"];
        Assert.Equal([12.5m, 13m], VolumeChapterScanner.ChaptersInNames(names));
    }

    [Theory]
    [InlineData("Title - ch049 - p001.png", 49)]
    [InlineData("Title Chapter 49 - 001.png", 49)]
    [InlineData("Chapter 007/page 001.png", 7)] // chapter grouped as a folder
    public void Recognizes_marker_variants(string name, int expected)
    {
        Assert.Equal([(decimal)expected], VolumeChapterScanner.ChaptersInNames([name]));
    }

    [Fact]
    public void Ignores_volume_and_page_markers()
    {
        // Only the chapter marker (c049) should be extracted, not v05 or p113.
        Assert.Equal([49m], VolumeChapterScanner.ChaptersInNames(["Series - c049 (v05) - p113.png"]));
    }

    [Theory]
    [InlineData("001.jpg")]
    [InlineData("Series v05 - p113.png")] // page marker only, no chapter
    [InlineData("Comic Compilation cover.png")] // 'c' words but no c-digit marker
    [InlineData("Arc049 promo.png")] // 'c' preceded by a letter is not a chapter marker
    public void Returns_empty_when_no_chapter_marker(string name)
    {
        Assert.Empty(VolumeChapterScanner.ChaptersInNames([name]));
    }

    [Fact]
    public void Unreadable_archive_yields_empty()
    {
        Assert.Empty(VolumeChapterScanner.ScanCbz(Path.Combine(Path.GetTempPath(), "does-not-exist.cbz")));
    }

    [Fact]
    public void BoundariesInNames_returns_first_page_index_per_chapter()
    {
        string[] names =
        [
            "x - c001 - p001.jpg", "x - c001 - p002.jpg", "x - c001 - p003.jpg", // 3 pages
            "x - c002 - p001.jpg", "x - c002 - p002.jpg",                       // 2 pages
            "x - c003 - p001.jpg",                                              // 1 page
        ];

        Assert.Equal([(1m, 0), (2m, 3), (3m, 5)], VolumeChapterScanner.BoundariesInNames(names));
    }

    [Fact]
    public void BoundariesInNames_ignores_pages_without_a_marker()
    {
        string[] names = ["cover.jpg", "x - c001 - p001.jpg", "x - c001 - p002.jpg"];

        Assert.Equal([(1m, 1)], VolumeChapterScanner.BoundariesInNames(names));
    }

    [Fact]
    public void ScanCbzBoundaries_reads_a_real_archive_in_page_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maki-bounds-{Guid.NewGuid():N}.cbz");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry("ComicInfo.xml");
                foreach (var page in new[]
                {
                    "x - c001 - p001.png", "x - c001 - p002.png",
                    "x - c002 - p001.png",
                })
                {
                    archive.CreateEntry(page, CompressionLevel.NoCompression);
                }
            }

            var (totalPages, boundaries) = VolumeChapterScanner.ScanCbzBoundaries(path);
            Assert.Equal(3, totalPages);
            Assert.Equal([(1m, 0), (2m, 2)], boundaries);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScanCbzBoundaries_unreadable_archive_yields_empty()
    {
        var (totalPages, boundaries) =
            VolumeChapterScanner.ScanCbzBoundaries(Path.Combine(Path.GetTempPath(), "does-not-exist.cbz"));
        Assert.Equal(0, totalPages);
        Assert.Empty(boundaries);
    }
}
