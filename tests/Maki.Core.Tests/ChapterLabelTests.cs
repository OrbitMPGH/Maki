using System.Globalization;
using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class ChapterLabelTests
{
    [Theory]
    [InlineData(10.5, null, "Ch.10.5")]
    [InlineData(24, 3, "Vol.3 Ch.24")]
    public void Labels_a_numbered_chapter(double number, int? volume, string expected) =>
        Assert.Equal(expected, ChapterLabel.For((decimal)number, volume, null, false));

    [Fact]
    public void A_one_shot_uses_its_title()
    {
        Assert.Equal("Extra", ChapterLabel.For(null, null, "Extra", true));
        Assert.Equal("One-shot", ChapterLabel.For(null, null, null, true));
    }

    [Fact]
    public void The_decimal_separator_ignores_the_process_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("Ch.10.5", ChapterLabel.For(10.5m, null, null, false));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
