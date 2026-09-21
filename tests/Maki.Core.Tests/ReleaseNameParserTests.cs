using Maki.Core.Parsing;

namespace Maki.Core.Tests;

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
    public void Parses_maki_own_names(string file, double number, int? volume)
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

    // Underscore-separated sets are the norm in older scanlation releases and none of them parsed:
    // _ is a word character, so the  the patterns used to start with found no boundary.
    [Theory]
    [InlineData("Narutaru_vol.0.rar", 0)]
    [InlineData("Narutaru_vol.12.rar", 12)]
    [InlineData("My_Series_v01.cbz", 1)]
    [InlineData("My_Series_Volume_3.cbz", 3)]
    public void Underscores_separate_a_volume_marker(string file, int volume)
    {
        var parsed = ReleaseNameParser.ParseFileName(file);
        Assert.True(parsed.IsVolume);
        Assert.Equal(volume, parsed.Volume);
    }

    [Fact]
    public void Underscores_separate_a_chapter_marker()
    {
        Assert.Equal(7m, ReleaseNameParser.ParseFileName("My_Series_ch07.cbz").Number);
        Assert.Equal(148m, ReleaseNameParser.ParseFileName("My_Series_148.cbz").Number);
    }

    // "vol.1" with nothing after it: GetFileNameWithoutExtension took the ".1" for an extension.
    [Theory]
    [InlineData("My Series vol.1", 1)]
    [InlineData("My Series vol.12", 12)]
    [InlineData("My Series vol.1.cbz", 1)]
    public void A_trailing_number_is_not_mistaken_for_an_extension(string file, int volume)
    {
        var parsed = ReleaseNameParser.ParseFileName(file);
        Assert.True(parsed.IsVolume);
        Assert.Equal(volume, parsed.Volume);
    }

    // The tag strip used to take the only marker the name had with it.
    [Theory]
    [InlineData("My Series (v01).cbz", 1)]
    [InlineData("My Series [Vol. 2].cbz", 2)]
    public void A_marker_inside_brackets_is_still_read(string file, int volume)
    {
        var parsed = ReleaseNameParser.ParseFileName(file);
        Assert.True(parsed.IsVolume);
        Assert.Equal(volume, parsed.Volume);
    }

    // The bracket retry only runs when the stripped name said nothing: "(v2)" is a second scan of
    // a chapter file, not its volume, and re-reading it as one would misfile every chapter it names.
    [Fact]
    public void A_name_that_already_parses_ignores_its_bracketed_tags()
    {
        var parsed = ReleaseNameParser.ParseFileName("Dandadan 148 (2024) (Digital) (1r0n) (v2).cbz");
        Assert.Equal(148m, parsed.Number);
        Assert.Null(parsed.Volume);
    }

    [Theory]
    [InlineData("Look Back.cbz")]
    [InlineData("My Series (2022) (Digital) (Group).cbz")]
    [InlineData("Some Random Extras.cbz")]
    public void A_name_with_no_number_at_all_stays_unrecognized(string file)
    {
        Assert.False(ReleaseNameParser.ParseFileName(file).IsRecognized);
    }

    // "c049" without the h is the scanlation convention, and VolumeChapterScanner already read it
    // off the page names inside an archive. An archive whose own name said c001 parsed as nothing.
    [Theory]
    [InlineData("Bokura no Hentai c001.cbz", 1)]
    [InlineData("My Series - c049 (v05).cbz", 49)]
    [InlineData("My_Series_c07.5.cbz", 7.5)]
    public void A_bare_c_is_a_chapter_marker(string file, double number)
    {
        Assert.Equal((decimal)number, ReleaseNameParser.ParseFileName(file).Number);
    }

    [Fact]
    public void A_word_ending_in_the_marker_letters_is_not_a_marker()
    {
        Assert.False(ReleaseNameParser.ParseFileName("Revolution 9 Arc.cbz").IsVolume);
        Assert.False(ReleaseNameParser.ParseFileName("March 5 Diaries.cbz").IsChapter);
        Assert.False(ReleaseNameParser.ParseFileName("Arc049 Notes.cbz").IsChapter);
        Assert.False(ReleaseNameParser.ParseFileName("Comic 5 Extras.cbz").IsChapter);
    }
}
