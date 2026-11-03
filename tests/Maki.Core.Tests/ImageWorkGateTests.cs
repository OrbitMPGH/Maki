using Maki.Core.Images;

namespace Maki.Core.Tests;

public class ImageWorkGateTests
{
    [Fact]
    public async Task Conversion_encodes_never_run_more_than_the_limit_at_once()
    {
        ImageWorkGate.SetConversionLimit(2);
        try
        {
            var running = 0;
            var peak = 0;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ImageWorkGate.RunConversionAsync(() =>
            {
                var now = Interlocked.Increment(ref running);
                lock (this)
                {
                    peak = Math.Max(peak, now);
                }

                Thread.Sleep(30);
                Interlocked.Decrement(ref running);
                return 0;
            }, 1, false, CancellationToken.None))));

            Assert.InRange(peak, 1, 2);
            Assert.Equal(2, ImageWorkGate.ConversionLimit);
        }
        finally
        {
            ImageWorkGate.SetConversionLimit(null);
        }

        Assert.Equal(ImageWorkGate.Parallelism, ImageWorkGate.ConversionLimit);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    public async Task Avif_encodes_take_half_the_limit_rounded_up(int limit, int avifAtOnce)
    {
        ImageWorkGate.SetConversionLimit(limit);
        try
        {
            Assert.Equal(avifAtOnce, ImageWorkGate.ConversionsInFlight(avif: true));
            Assert.Equal(limit, ImageWorkGate.ConversionsInFlight(avif: false));
            var running = 0;
            var avifRunning = 0;
            var peak = 0;
            var avifPeak = 0;
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => ImageWorkGate.RunConversionAsync(() =>
            {
                var avif = i % 2 == 0;
                var now = Interlocked.Increment(ref running);
                var avifNow = avif ? Interlocked.Increment(ref avifRunning) : 0;
                lock (this)
                {
                    peak = Math.Max(peak, now);
                    avifPeak = Math.Max(avifPeak, avifNow);
                }

                Thread.Sleep(30);
                if (avif)
                {
                    Interlocked.Decrement(ref avifRunning);
                }

                Interlocked.Decrement(ref running);
                return 0;
            }, 1, i % 2 == 0, CancellationToken.None))));

            Assert.InRange(peak, 1, limit);
            Assert.InRange(avifPeak, 1, avifAtOnce);
        }
        finally
        {
            ImageWorkGate.SetConversionLimit(null);
        }

        Assert.Equal((ImageWorkGate.Parallelism + 1) / 2, ImageWorkGate.ConversionsInFlight(avif: true));
    }

    [Fact]
    public void A_limit_past_the_core_count_is_clamped()
    {
        ImageWorkGate.SetConversionLimit(ImageWorkGate.MaxConversionLimit + 10);
        try
        {
            Assert.Equal(ImageWorkGate.MaxConversionLimit, ImageWorkGate.ConversionLimit);
        }
        finally
        {
            ImageWorkGate.SetConversionLimit(null);
        }
    }
}
