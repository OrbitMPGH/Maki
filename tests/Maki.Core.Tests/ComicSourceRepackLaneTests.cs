using Maki.Core.Import;
using Xunit;

namespace Maki.Core.Tests;

/// <summary>
/// Archive repacks share two fixed threads, so a library import of eight solid 7z files holds two
/// decoder windows instead of eight.
/// </summary>
public class ComicSourceRepackLaneTests
{
    [Fact]
    public void No_moreThanTwoRepacks_runAtOnce()
    {
        var running = 0;
        var peak = 0;
        var callers = Enumerable.Range(0, 6).Select(_ => new Thread(() => ComicSourceConverter.RunOnRepackLane(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(100);
            Interlocked.Decrement(ref running);
        }))).ToList();

        callers.ForEach(t => t.Start());
        callers.ForEach(t => t.Join());

        Assert.Equal(ComicSourceConverter.MaxConcurrentRepacks, peak);
    }

    [Fact]
    public void A_failure_reachesTheCaller_asItsOwnType()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            ComicSourceConverter.RunOnRepackLane(() => throw new InvalidDataException("bad archive")));
        Assert.Equal("bad archive", ex.Message);

        var ran = false;
        ComicSourceConverter.RunOnRepackLane(() => ran = true);
        Assert.True(ran);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
