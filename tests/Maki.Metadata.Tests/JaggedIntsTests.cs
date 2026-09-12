using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

public class JaggedIntsTests
{
    [Fact]
    public void From_FlattensRowsAndKeepsTheirBoundaries()
    {
        var jagged = JaggedInts.From([[1, 2, 3], [], [4], [5, 6]]);

        Assert.Equal(4, jagged.Count);
        Assert.Equal([1, 2, 3], jagged[0].ToArray());
        Assert.Empty(jagged[1].ToArray());
        Assert.Equal([4], jagged[2].ToArray());
        Assert.Equal([5, 6], jagged[3].ToArray());
    }

    /// <summary>
    /// A null row is how the dump spells "no genres", and every consumer already read it as empty.
    /// Flattening has to keep that rather than shifting the rows after it, which is the one way
    /// this could go wrong silently: the values would all still be present, just attributed to the
    /// wrong series.
    /// </summary>
    [Fact]
    public void From_TreatsANullRowAsEmptyWithoutShiftingTheRest()
    {
        var jagged = JaggedInts.From([[7], null, [8, 9]]);

        Assert.Equal(3, jagged.Count);
        Assert.Equal([7], jagged[0].ToArray());
        Assert.Empty(jagged[1].ToArray());
        Assert.Equal([8, 9], jagged[2].ToArray());
    }

    [Fact]
    public void Empty_HasNoRows()
    {
        Assert.Equal(0, JaggedInts.Empty.Count);
        Assert.Equal(0, JaggedInts.From([]).Count);
    }
}
