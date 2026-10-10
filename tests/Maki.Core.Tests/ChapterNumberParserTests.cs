using System.Globalization;
using Maki.Core.Parsing;

namespace Maki.Core.Tests;

public class ChapterNumberParserTests
{
    [Theory]
    [InlineData("10", 10, null, false)]
    [InlineData("10.5", 10.5, null, false)]
    [InlineData("110.1", 110.1, null, false)]
    [InlineData("Ch. 10", 10, null, false)]
    [InlineData("Ch.10.5", 10.5, null, false)]
    [InlineData("Chapter 100", 100, null, false)]
    [InlineData("chapter 42.5", 42.5, null, false)]
    [InlineData("#12", 12, null, false)]
    [InlineData("100 - The Ending", 100, null, false)]
    [InlineData("5.5: Extras", 5.5, null, false)]
    [InlineData("Episode 124", 124, null, false)]
    [InlineData("Ep. 7.5", 7.5, null, false)]
    [InlineData("Ch10", 10, null, false)]
    [InlineData("Chapter10", 10, null, false)]
    [InlineData("Ep5", 5, null, false)]
    [InlineData("Ch.6,1", 6.1, null, false)]
    [InlineData("Chapter 6,5", 6.5, null, false)]
    [InlineData("6,5", 6.5, null, false)]
    [InlineData("6,5 - Extras", 6.5, null, false)]
    [InlineData("Ch.9,10", 9, null, false)]
    [InlineData("Ch. 1,2", 1.2, null, false)]
    [InlineData("Ch.4,5", 4.5, null, false)]
    [InlineData("4,5", 4.5, null, false)]
    [InlineData("0,1", 0.1, null, false)]
    [InlineData("6,05", 6.05, null, false)]
    [InlineData("Chapter 10,11", 10, null, false)]
    [InlineData("12,13", 12, null, false)]
    [InlineData("Ch.6,10", 6.1, null, false)]
    public void Parses_chapter_numbers(string input, double expected, int? volume, bool oneShot)
    {
        var result = ChapterNumberParser.Parse(input);
        Assert.Equal((decimal)expected, result.Number);
        Assert.Equal(volume, result.Volume);
        Assert.Equal(oneShot, result.IsOneShot);
    }

    [Theory]
    [InlineData("Vol.3 Ch.24", 24, 3)]
    [InlineData("Vol. 3 Chapter 24.5", 24.5, 3)]
    [InlineData("Volume 2 Ch. 8", 8, 2)]
    public void Parses_embedded_volumes(string input, double number, int volume)
    {
        var result = ChapterNumberParser.Parse(input);
        Assert.Equal((decimal)number, result.Number);
        Assert.Equal(volume, result.Volume);
    }

    [Theory]
    [InlineData("Oneshot")]
    [InlineData("One-shot")]
    [InlineData("one shot")]
    public void Detects_oneshots(string input)
    {
        var result = ChapterNumberParser.Parse(input);
        Assert.True(result.IsOneShot);
        Assert.Null(result.Number);
    }

    [Fact]
    public void Separate_volume_string_is_used()
    {
        var result = ChapterNumberParser.Parse("24", "3");
        Assert.Equal(24m, result.Number);
        Assert.Equal(3, result.Volume);
    }

    [Fact]
    public void Null_chapter_with_volume_is_not_oneshot()
    {
        var result = ChapterNumberParser.Parse(null, "2");
        Assert.Null(result.Number);
        Assert.Equal(2, result.Volume);
        Assert.False(result.IsOneShot);
    }

    [Fact]
    public void Null_chapter_without_volume_is_oneshot()
    {
        var result = ChapterNumberParser.Parse(null);
        Assert.True(result.IsOneShot);
    }

    [Theory]
    [InlineData("Chapters")]
    [InlineData("Episodes 5")]
    [InlineData("1,000")]
    public void Keywords_with_more_letters_and_thousands_separators_are_not_chapter_numbers(string label)
    {
        var result = ChapterNumberParser.Parse(label);
        Assert.Null(result.Number);
    }

    [Theory]
    [InlineData("Vol. 3 Extras", 3)]
    [InlineData("Volume 2", 2)]
    public void Unparseable_text_with_a_volume_is_a_volume_row_not_a_oneshot(string label, int volume)
    {
        var result = ChapterNumberParser.Parse(label);
        Assert.Null(result.Number);
        Assert.Equal(volume, result.Volume);
        Assert.False(result.IsOneShot);
    }

    [Fact]
    public void Unparseable_text_falls_back_to_oneshot()
    {
        var result = ChapterNumberParser.Parse("Special Extra Bonus");
        Assert.True(result.IsOneShot);
        Assert.Null(result.Number);
    }

    [Fact]
    public void Overflowing_embedded_volume_is_treated_as_no_volume()
    {
        var result = ChapterNumberParser.Parse("Vol.99999999999999999999 Ch.24");
        Assert.Equal(24m, result.Number);
        Assert.Null(result.Volume);
    }

    [Fact]
    public void Overflowing_separate_volume_string_is_treated_as_no_volume()
    {
        var result = ChapterNumberParser.Parse("24", "99999999999999999999");
        Assert.Equal(24m, result.Number);
        Assert.Null(result.Volume);
    }

    [Theory]
    [InlineData("https://www.topmanhua.fan/manhua/anjo/chapter-225", "225")]
    [InlineData("https://toonily.com/serie/secret-class/chapter-242/", "242")]
    [InlineData("https://toonily.com/serie/secret-class/chapter-12-5/", "12.5")]
    [InlineData("7991/one-piece-chapter-1187", "1187")]
    [InlineData("/chapters/1-20385000/berserk-chapter-385", "385")]
    [InlineData("chapter-652", "652")]
    [InlineData("https://mangakatana.com/manga/slug.123/c1050.5", "1050.5")]
    [InlineData("7992/one-piece-oneshot", null)]
    [InlineData("https://mangadex.org/chapter/0b1f5e0e-3c0e-4a7d-9d4e-000000000000", null)]
    [InlineData("chapter-99999999999999999999999999999999", null)]
    [InlineData(null, null)]
    public void Slug_number_is_read_from_the_end_of_a_chapter_url(string? url, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, CultureInfo.InvariantCulture),
            ChapterNumberParser.FromSlug(url));

    [Fact]
    public void A_title_only_label_takes_its_number_from_the_slug()
    {
        var result = ChapterNumberParser.Parse("Anna-chan Can't Study").OrSlugNumber("/manhua/anjo/chapter-224");
        Assert.Equal(224m, result.Number);
        Assert.False(result.IsOneShot);
    }

    [Fact]
    public void A_numbered_label_wins_over_the_slug()
    {
        var result = ChapterNumberParser.Parse("Chapter 10").OrSlugNumber("/chapter-11");
        Assert.Equal(10m, result.Number);
    }

    // \d matches any Unicode digit but decimal.Parse only takes ASCII, so these used to throw.
    [Theory]
    [InlineData("\u0661\u0662")]
    [InlineData("Ch. \u0661\u0662")]
    [InlineData("\u0661\u0662 - Title")]
    public void Non_ascii_digits_read_as_unnumbered_rather_than_throwing(string label)
    {
        var result = ChapterNumberParser.Parse(label);
        Assert.Null(result.Number);
        Assert.True(result.IsOneShot);
    }

    [Theory]
    [InlineData("\uFF11\uFF12", 12)]
    [InlineData("Ch. \uFF11\uFF12", 12)]
    [InlineData("\uFF23\uFF48. \uFF11\uFF10.\uFF15", 10.5)]
    public void Fullwidth_digits_read_as_their_ascii_number(string label, double expected)
    {
        var result = ChapterNumberParser.Parse(label);
        Assert.Equal((decimal)expected, result.Number);
        Assert.False(result.IsOneShot);
    }

    [Fact]
    public void A_fullwidth_volume_string_is_read()
    {
        Assert.Equal(3, ChapterNumberParser.Parse("12", "\uFF13").Volume);
    }
}
