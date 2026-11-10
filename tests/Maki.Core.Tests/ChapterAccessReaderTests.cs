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
    [InlineData("（Preview）2巻", ChapterAccess.Preview)]
    [InlineData("第1巻 試し読み", ChapterAccess.Preview)]
    [InlineData("(1)", ChapterAccess.Preview)]
    [InlineData("（１）", ChapterAccess.Preview)]
    [InlineData(" （１０） ", ChapterAccess.Preview)]
    [InlineData("隣のステラ（１）", ChapterAccess.Free)]
    [InlineData("第1話(1)", ChapterAccess.Free)]
    [InlineData("【期間限定 無料お試し版 閲覧期限2026年11月4日】", ChapterAccess.Preview)]
    [InlineData("第5話 無料お試し", ChapterAccess.Preview)]
    [InlineData("【期間限定無料】第12話", ChapterAccess.Free)]
    [InlineData("第3話 閲覧期限なし", ChapterAccess.Free)]
    [InlineData("第1話 お試し同棲はじめました", ChapterAccess.Free)]
    [InlineData("お試し結婚 第4話", ChapterAccess.Free)]
    [InlineData(null, ChapterAccess.Free)]
    [InlineData("", ChapterAccess.Free)]
    public void Reads_the_access_a_label_announces(string? label, ChapterAccess expected) =>
        Assert.Equal(expected, ChapterAccessReader.Of(label));
}
