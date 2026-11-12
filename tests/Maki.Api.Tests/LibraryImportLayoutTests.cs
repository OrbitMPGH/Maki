using System.IO.Compression;
using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// Which folders and loose files a root's layout offers to the import scan: grouping folders such
/// as <c>A-Z</c> are walked into, a series folder split into volume folders is not, and comics left
/// directly in the root are grouped by the series their names carry.
/// </summary>
public class LibraryImportLayoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-import-layout-" + Guid.NewGuid().ToString("N")[..8]);

    public LibraryImportLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteZip(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write("page");
    }

    private List<string> Folders(params string[] ignored) =>
        LibraryImportService.ImportFolders(_root, new HashSet<string>(), ignored.ToHashSet())
            .Select(f => f.Name)
            .ToList();

    [Fact]
    public void Grouping_folders_are_walked_into_and_series_folders_are_not()
    {
        WriteZip("A-Z", "Berserk", "Berserk v01.cbz");
        WriteZip("A-Z", "Monster", "Monster v01.cbz");
        WriteZip("Viz", "Shonen", "Naruto", "Naruto v01.cbz");
        WriteZip("Vagabond", "Vagabond v01.cbz");

        Assert.Equal(["A-Z/Berserk", "A-Z/Monster", "Vagabond", "Viz/Shonen/Naruto"], Folders());
    }

    [Fact]
    public void A_series_split_into_volume_folders_is_one_folder()
    {
        WriteZip("Berserk", "Vol. 01", "Berserk c001.cbz");
        WriteZip("Berserk", "Vol. 02", "Berserk c010.cbz");
        Directory.CreateDirectory(Path.Combine(_root, "Loose", "Chapter One"));
        File.WriteAllText(Path.Combine(_root, "Loose", "Chapter One", "001.png"), "page");

        Assert.Equal(["Berserk", "Loose"], Folders());
    }

    [Fact]
    public void An_ignored_nested_folder_is_skipped_and_depth_is_capped()
    {
        WriteZip("A-Z", "Berserk", "Berserk v01.cbz");
        WriteZip("A-Z", "Extras", "Art v01.cbz");
        WriteZip("One", "Two", "Three", "Four", "Series v01.cbz");

        Assert.Equal(["A-Z/Berserk", "One/Two/Three"], Folders("A-Z/Extras"));
    }

    [Fact]
    public void Loose_root_files_are_grouped_by_series_into_free_folder_names()
    {
        WriteZip("Berserk v01.cbz");
        WriteZip("Berserk v02.cbz");
        WriteZip("Monster 001.cbz");
        WriteZip("Monster", "something.cbz");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a comic");

        var groups = LibraryImportService.LooseFileGroups(_root);

        var berserk = Assert.Single(groups, g => g.SeriesName == "Berserk");
        Assert.Equal("Berserk", berserk.FolderName);
        Assert.Equal(["Berserk v01.cbz", "Berserk v02.cbz"], berserk.Files);
        var monster = Assert.Single(groups, g => g.SeriesName == "Monster");
        Assert.Equal("Monster (2)", monster.FolderName);
    }

    [Theory]
    [InlineData("Berserk", true)]
    [InlineData("A-Z/Berserk", true)]
    [InlineData("Viz/Shonen/Naruto", true)]
    [InlineData("A/B/C/D", false)]
    [InlineData("A-Z/../Berserk", false)]
    [InlineData("A-Z\\Berserk", false)]
    [InlineData("/Berserk", false)]
    [InlineData("", false)]
    public void Only_plain_relative_folder_paths_are_accepted(string name, bool valid) =>
        Assert.Equal(valid, LibraryImportService.IsImportFolderPath(name));
}
