using Maki.Sources.Common;

namespace Maki.Sources.Tests;

public class CjkChapterNumberTests
{
    [Theory]
    [InlineData("第1186话 title", 1186)]
    [InlineData("第 12.5 話", 12.5)]
    [InlineData("第３回", 3)]
    [InlineData("第１２话", 12)]
    public void Chapter_reads_ascii_and_fullwidth_digits(string label, double expected)
    {
        Assert.Equal((decimal)expected, CjkChapterNumber.Chapter(label));
    }

    [Theory]
    [InlineData("番外篇")]
    [InlineData("第٣话")]
    public void Chapter_is_null_without_a_readable_number(string label)
    {
        Assert.Null(CjkChapterNumber.Chapter(label));
    }

    [Fact]
    public void Volume_reads_fullwidth_digits()
    {
        Assert.Equal(7, CjkChapterNumber.Volume("第７卷"));
        Assert.Null(CjkChapterNumber.Volume("第1话"));
    }
}
