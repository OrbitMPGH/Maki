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
            }, 1, CancellationToken.None))));

            Assert.InRange(peak, 1, 2);
            Assert.Equal(2, ImageWorkGate.ConversionLimit);
        }
        finally
        {
            ImageWorkGate.SetConversionLimit(null);
        }

        Assert.Equal(ImageWorkGate.Parallelism, ImageWorkGate.ConversionLimit);
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
