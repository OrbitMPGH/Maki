using Maki.Core.Entities;
using Maki.Core.Naming;

namespace Maki.Core.Tests;

public class FileNameBuilderTests
{
    private static Series SeriesFor(string title) => new() { Title = title, FolderName = FileNameSanitizer.Sanitize(title) };

    [Fact]
    public void Volume_and_chapter()
    {
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("Berserk"),
            new Chapter { Number = 24, Volume = 3 });
        Assert.Equal("Berserk Vol.3 Ch.24.cbz", name);
    }

    [Fact]
    public void Group_reaches_the_name_and_the_relative_path()
    {
        const string format = "{Series Title} {Chapter VolChap} [{Chapter Group}]";
        var series = SeriesFor("Berserk");
        var chapter = new Chapter { Number = 5 };

        Assert.Equal("Berserk Ch.5 [Group Name].cbz",
            FileNameBuilder.BuildChapterFileName(series, chapter, format, "Group Name"));
        Assert.Equal("Berserk Ch.5.cbz", FileNameBuilder.BuildChapterFileName(series, chapter, format));
        Assert.Equal(Path.Combine("Berserk", "Berserk Ch.5 [Group Name].cbz"),
            FileNameBuilder.BuildRelativePath(series, chapter, format, "Group Name"));
    }

    [Fact]
    public void Volume_less_decimal_chapter()
    {
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("One Punch Man"),
            new Chapter { Number = 10.5m });
        Assert.Equal("One Punch Man Ch.10.5.cbz", name);
    }

    [Fact]
    public void One_shot_uses_series_name()
    {
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("Look Back"),
            new Chapter { IsOneShot = true });
        Assert.Equal("Look Back.cbz", name);
    }

    [Fact]
    public void Illegal_characters_are_stripped()
    {
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("Re:Zero? <Test>"),
            new Chapter { Number = 1 });
        Assert.Equal("ReZero Test Ch.1.cbz", name);
    }

    [Fact]
    public void A_leading_dot_is_dropped_so_the_folder_is_not_hidden()
    {
        var series = new Series { Title = ".hack//G.U.+", FolderName = "x" };
        Assert.Equal("hackG.U.+", FileNameBuilder.BuildSeriesFolderName(series));
        Assert.Equal("hackG.U.+ Ch.1.cbz", FileNameBuilder.BuildChapterFileName(series, new Chapter { Number = 1 }));
    }

    [Theory]
    [InlineData("Title .", "Title")]
    [InlineData("Title . .", "Title")]
    [InlineData(". .hack", "hack")]
    public void Dots_and_spaces_are_trimmed_until_neither_edge_has_one(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("Aux", "Aux_")]
    [InlineData("nul", "nul_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("Con.Air", "Con_.Air")]
    [InlineData("Aux Ch.1", "Aux Ch.1")]
    [InlineData("Console", "Console")]
    public void Windows_device_names_get_an_underscore(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void A_name_within_the_byte_limit_is_left_alone()
    {
        var name = new string('a', FileNameSanitizer.MaxBytes);
        Assert.Equal(name, FileNameSanitizer.Sanitize(name));
    }

    [Fact]
    public void An_over_long_name_keeps_its_start_and_end_within_the_limit()
    {
        // About 85 CJK characters already reach 255 bytes, the ext4 limit for one name.
        var title = string.Concat(Enumerable.Repeat("\u9032\u6483\u306E\u5DE8\u4EBA", 30));
        var name = FileNameSanitizer.Sanitize(title + " Vol.12 Ch.105");

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= FileNameSanitizer.MaxBytes);
        Assert.StartsWith("\u9032\u6483\u306E", name);
        Assert.EndsWith(" Vol.12 Ch.105", name);
    }

    [Fact]
    public void Over_long_names_that_differ_only_in_the_middle_stay_distinct()
    {
        var head = new string('a', 150);
        var tail = new string('z', 100);
        Assert.NotEqual(
            FileNameSanitizer.Sanitize(head + " one " + tail),
            FileNameSanitizer.Sanitize(head + " two " + tail));
    }

    [Fact]
    public void Shortening_never_splits_a_surrogate_pair()
    {
        var name = FileNameSanitizer.Sanitize(string.Concat(Enumerable.Repeat("\U0001F600", 100)));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= FileNameSanitizer.MaxBytes);
        Assert.DoesNotContain('\uFFFD', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(name)));
    }

    [Fact]
    public void Relative_path_includes_series_folder()
    {
        var series = SeriesFor("Berserk");
        var path = FileNameBuilder.BuildRelativePath(series, new Chapter { Number = 1 });
        Assert.Equal(Path.Combine("Berserk", "Berserk Ch.1.cbz"), path);
    }

    // Chapter identity is (Number, Language), so a series synced in two languages has two rows
    // wanting chapter 24 — and the default format has no {Chapter Language} to separate them.

    [Fact]
    public void English_is_not_suffixed()
    {
        // Every file already on disk is English. Suffixing it would rename the whole library.
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("Berserk"),
            new Chapter { Number = 24, Volume = 3, Language = "en" });
        Assert.Equal("Berserk Vol.3 Ch.24.cbz", name);
    }

    [Fact]
    public void Another_language_is_suffixed_with_its_code()
    {
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("Berserk"),
            new Chapter { Number = 24, Volume = 3, Language = "es" });
        Assert.Equal("Berserk Vol.3 Ch.24 [es].cbz", name);
    }

    [Fact]
    public void A_format_that_names_the_language_itself_gets_no_suffix()
    {
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("Berserk"),
            new Chapter { Number = 24, Volume = 3, Language = "es" },
            "{Series Title} {Chapter VolChap} ({Chapter Language})");
        Assert.Equal("Berserk Vol.3 Ch.24 (es).cbz", name);
    }

    [Theory]
    [InlineData("{Chapter.Language}")]
    [InlineData("{chapter_language}")]
    [InlineData("{ChapterLanguage}")]
    public void A_language_token_spelled_with_other_separators_gets_no_suffix(string token)
    {
        var name = FileNameBuilder.BuildChapterFileName(
            SeriesFor("Berserk"),
            new Chapter { Number = 24, Language = "es" },
            "{Series Title} {Chapter VolChap} " + token);
        Assert.Equal("Berserk Ch.24 es.cbz", name);
    }

    [Fact]
    public void The_suffix_reaches_the_relative_path_too()
    {
        var path = FileNameBuilder.BuildRelativePath(
            SeriesFor("Berserk"), new Chapter { Number = 1, Language = "ja" });
        Assert.Equal(Path.Combine("Berserk", "Berserk Ch.1 [ja].cbz"), path);
    }
}
