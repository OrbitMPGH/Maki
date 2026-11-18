using Maki.Metadata.Embedding;

namespace Maki.Metadata.Tests;

public class ReasonShareTests
{
    [Fact]
    public void Keep_OrdersByContribution_AndDropsSeedsUnderTheShareOfThePositiveTotal()
    {
        (long, double)[] contributions = [(1, 0.1), (2, 0.6), (3, 0.3), (4, -0.5)];

        Assert.Equal([2L, 3L, 1L], ReasonShare.Keep(contributions, 0));
        Assert.Equal([2L, 3L], ReasonShare.Keep(contributions, 0.25));
        Assert.Empty(ReasonShare.Keep(contributions, 0.7));
    }
}
