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

    [Fact]
    public void Unparseable_text_falls_back_to_oneshot()
    {
        var result = ChapterNumberParser.Parse("Special Extra Bonus");
        Assert.True(result.IsOneShot);
        Assert.Null(result.Number);
    }
}
