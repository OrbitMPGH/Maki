using System.IO.Compression;
using Mangarr.Core.Parsing;

namespace Mangarr.Core.Tests;

public class VolumeChapterScannerTests
{
    [Fact]
    public void Scans_a_real_compilation_archive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mangarr-vol-{Guid.NewGuid():N}.cbz");
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
}
