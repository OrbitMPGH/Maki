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
            }, new ConversionWeight(1, false), CancellationToken.None))));

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
        // The limit clamps to the core count, so a small runner gets a lower one than asked.
        if (limit > ImageWorkGate.MaxConversionLimit)
        {
            limit = ImageWorkGate.MaxConversionLimit;
            avifAtOnce = (limit + 1) / 2;
        }

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
            }, new ConversionWeight(1, i % 2 == 0), CancellationToken.None))));

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
    public async Task Encodes_that_together_pass_the_memory_budget_run_one_at_a_time()
    {
        ImageWorkGate.SetConversionLimit(Math.Min(4, ImageWorkGate.MaxConversionLimit));
        try
        {
            var running = 0;
            var peak = 0;
            var heavy = new ConversionWeight(ImageWorkGate.ConversionBudgetMegabytes / 2 + 1, false);
            var oversized = new ConversionWeight(ImageWorkGate.ConversionBudgetMegabytes * 3, false);
            await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => ImageWorkGate.RunConversionAsync(() =>
            {
                var now = Interlocked.Increment(ref running);
                lock (this)
                {
                    peak = Math.Max(peak, now);
                }

                Thread.Sleep(20);
                Interlocked.Decrement(ref running);
                return 0;
            }, i == 5 ? oversized : heavy, CancellationToken.None))));

            Assert.Equal(1, peak);
        }
        finally
        {
            ImageWorkGate.SetConversionLimit(null);
        }
    }

    [Fact]
    public void An_encode_is_weighed_by_its_encoder_memory()
    {
        Assert.InRange(ImageWorkGate.ConversionBudgetMegabytes, 64, 1024);
        var avif = ImageWorkGate.WeighConversion(1115, 1600, 3, PageEncoding.Avif);
        Assert.Equal(new ConversionWeight(1115 * 1600 * 50 / (1 << 20) + 10, true), avif);
        var colour = ImageWorkGate.WeighConversion(1115, 1600, 3, PageEncoding.Webp);
        var grey = ImageWorkGate.WeighConversion(1115, 1600, 1, PageEncoding.Webp);
        var lossless = ImageWorkGate.WeighConversion(1115, 1600, 3, PageEncoding.LosslessWebp);
        Assert.True(grey.Megabytes < colour.Megabytes && colour.Megabytes < lossless.Megabytes && lossless.Megabytes < avif.Megabytes);
        Assert.False(colour.Avif);
        Assert.Equal(ImageWorkGate.ConversionBudgetMegabytes,
            ImageWorkGate.WeighConversion(16_000, 16_000, 3, PageEncoding.Avif).Megabytes);

        var settings = new PageConversionSettings(PageConversionFormat.Avif, PageConversionStop.High, false);
        var page = ArchivePageConverterTests.Scan(600, 900, 1).EncodeJpeg(95);
        Assert.Equal(ImageWorkGate.WeighConversion(600, 900, 3, PageEncoding.Avif), PageConversion.WeightOf(page, ".jpg", settings));
        Assert.Equal(ImageWorkGate.LightConversion, PageConversion.WeightOf(page, ".jpg",
            new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.Lossless, false)));
        Assert.Equal(ImageWorkGate.LightConversion, PageConversion.WeightOf("not an image"u8.ToArray(), ".jpg", settings));
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
