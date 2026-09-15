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

    [Fact]
    public void The_suffix_reaches_the_relative_path_too()
    {
        var path = FileNameBuilder.BuildRelativePath(
            SeriesFor("Berserk"), new Chapter { Number = 1, Language = "ja" });
        Assert.Equal(Path.Combine("Berserk", "Berserk Ch.1 [ja].cbz"), path);
    }
}
