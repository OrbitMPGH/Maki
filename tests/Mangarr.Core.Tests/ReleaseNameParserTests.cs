using Mangarr.Core.Parsing;

namespace Mangarr.Core.Tests;

// Test corpus taken from a real library share — these exact names must keep parsing.
public class ReleaseNameParserTests
{
    [Theory]
    [InlineData("Dandadan (Digital) (1r0n)", "Dandadan")]
    [InlineData("At Home with a Girl in Her Cute Pajamas (2026) (Digital) (Oak)", "At Home with a Girl in Her Cute Pajamas")]
    [InlineData("Backstabbed in a Backwater Dungeon [J-Novel Club] [CleanBookGuy]", "Backstabbed in a Backwater Dungeon")]
    [InlineData("Insomniacs After School (2023-2026) (Digital) (1r0n + kaOak)", "Insomniacs After School")]
    [InlineData("A Prince of a Friend", "A Prince of a Friend")]
    [InlineData("DARLING in the FRANXX (2022) (Digital) (1r0n)", "DARLING in the FRANXX")]
    public void Cleans_folder_titles(string folder, string expected)
    {
        Assert.Equal(expected, ReleaseNameParser.CleanFolderTitle(folder));
    }

    [Theory]
    [InlineData("A Prince of a Friend Chapter 0000.cbz", 0, null, null)]
    [InlineData("Chanto Suki tte Ieruko Musou Chapter 0002.cbz", 2, null, null)]
    [InlineData("Dandadan 148 (2024) (Digital) (1r0n).cbz", 148, null, null)]
    [InlineData("I Want to End This Love Game 049.1 (2024) (Digital) (1r0n).cbz", 49.1, null, null)]
    [InlineData("I Want to End This Love Game 050 (2024) (Digital) (1r0n).cbz", 50, null, null)]
    public void Parses_chapter_files(string file, double number, int? volume, int? volumeEnd)
    {
        var parsed = ReleaseNameParser.ParseFileName(file);
        Assert.True(parsed.IsChapter);
        Assert.Equal((decimal)number, parsed.Number);
        Assert.Equal(volume, parsed.Volume);
        Assert.Equal(volumeEnd, parsed.VolumeEnd);
    }

    [Theory]
    [InlineData("At Home with a Girl in Her Cute Pajamas v01 (Digital-Compilation) (Oak).cbz", 1, null)]
    [InlineData("Boyish Girlfriend v02 (Digital-Compilation) (Oak).cbz", 2, null)]
    [InlineData("Insomniacs After School v01 (2023) (Digital) (1r0n).cbz", 1, null)]
    [InlineData("DARLING in the FRANXX v01-02 (2022) (Digital) (1r0n) (f).cbz", 1, 2)]
    [InlineData("DARLING in the FRANXX v03-04 (2022) (Digital) (1r0n).cbz", 3, 4)]
    public void Parses_volume_files(string file, int volume, int? volumeEnd)
    {
        var parsed = ReleaseNameParser.ParseFileName(file);
        Assert.True(parsed.IsVolume);
        Assert.Equal(volume, parsed.Volume);
        Assert.Equal(volumeEnd, parsed.VolumeEnd);
    }

    [Theory]
    [InlineData("Berserk Vol.3 Ch.24.cbz", 24, 3)]
    [InlineData("One Punch Man Ch.10.5.cbz", 10.5, null)]
    public void Parses_mangarr_own_names(string file, double number, int? volume)
    {
        var parsed = ReleaseNameParser.ParseFileName(file);
        Assert.Equal((decimal)number, parsed.Number);
        Assert.Equal(volume, parsed.Volume);
    }

    [Fact]
    public void Unrecognized_names_are_flagged()
    {
        var parsed = ReleaseNameParser.ParseFileName("Some Random Extras.cbz");
        Assert.False(parsed.IsRecognized);
    }

    [Fact]
    public void Year_tags_do_not_read_as_chapter_numbers()
    {
        var parsed = ReleaseNameParser.ParseFileName("Look Back (2024) (Digital) (Oak).cbz");
        Assert.False(parsed.IsChapter);
    }
}
