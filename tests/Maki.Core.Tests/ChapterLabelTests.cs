using System.Globalization;
using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class ChapterLabelTests
{
    [Fact]
    public void A_decimal_chapter_keeps_its_point_on_a_comma_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal("Ch.10.5", ChapterLabel.For(10.5m, null, null, false));
            Assert.Equal("Vol.2 Ch.10.5", ChapterLabel.For(10.5m, 2, null, false));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
