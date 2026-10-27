using Maki.Core.Parsing;

namespace Maki.Core.Tests;

public class ChapterAccessReaderTests
{
    [Theory]
    [InlineData("第1話", ChapterAccess.Free)]
    [InlineData("Ch. 10 - The Fight", ChapterAccess.Free)]
    [InlineData("\U0001F193 第1話", ChapterAccess.Free)]
    [InlineData("\U0001F512 第14話(1)", ChapterAccess.Locked)]
    [InlineData("\U0001F4B4 第9話", ChapterAccess.Locked)]
    [InlineData("\U0001F512 (Preview) 1巻", ChapterAccess.Preview)]
    [InlineData("(Preview) 隣のステラ（１）", ChapterAccess.Preview)]
    [InlineData(null, ChapterAccess.Free)]
    [InlineData("", ChapterAccess.Free)]
    public void Reads_the_access_a_label_announces(string? label, ChapterAccess expected) =>
        Assert.Equal(expected, ChapterAccessReader.Of(label));
}
