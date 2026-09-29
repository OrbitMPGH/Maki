using Maki.Core.Entities;
using Maki.Core.Quality;

namespace Maki.Core.Tests.Quality;

public class SourceQualityEstimateTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private static SourceQualitySample Sample(int width, double bitsPerPixel, string format = "jpg", int daysOld = 1,
        int height = 1500, int pages = 20) => new()
    {
        PageCount = pages, MedianWidth = width, MedianHeight = height, ImageFormat = format,
        SizeBytes = (long)(bitsPerPixel * width * height / 8) * pages, MeasuredAtUtc = Now.AddDays(-daysOld)
    };

    [Fact]
    public void Medians_resist_one_odd_chapter()
    {
        var estimate = SourceQualityEstimate.From([Sample(1000, 1.5), Sample(1000, 1.6), Sample(2400, 9.0, "png")])!;

        Assert.Equal(3, estimate.Samples);
        Assert.Equal(1000, estimate.MedianWidth);
        Assert.Equal(1.6, estimate.BitsPerPixel, 2);
        Assert.Equal("jpg", estimate.ImageFormat);
    }

    [Fact]
    public void Mixed_formats_are_compared_as_jpg_equivalent_before_the_median()
    {
        var estimate = SourceQualityEstimate.From([Sample(1000, 2.0, "png"), Sample(1000, 2.0, "png"), Sample(1000, 1.3)])!;

        Assert.Equal(1.3, estimate.BitsPerPixel, 2);
    }

    [Fact]
    public void Unusable_samples_are_ignored()
    {
        Assert.Null(SourceQualityEstimate.From([Sample(1000, 1.5, pages: 0)]));
        Assert.Equal(1, SourceQualityEstimate.From([Sample(1000, 1.5), Sample(1000, 1.5, height: 0)])!.Samples);
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 61, false)]
    public void Reliable_needs_enough_recent_samples(int count, int daysOld, bool expected)
    {
        var estimate = SourceQualityEstimate.From([.. Enumerable.Range(0, count).Select(_ => Sample(1000, 1.5, daysOld: daysOld))])!;

        Assert.Equal(expected, estimate.IsReliable(Now));
    }

    [Fact]
    public void An_applied_estimate_scores_like_the_copies_it_came_from_and_keeps_the_listing_page_count()
    {
        var profile = new UpgradeProfile { ResolutionWeight = 10, CompressionWeight = 10 };
        var measured = new QualityCandidate(QualityTier.Aggregator, null, null, null, null, 20, 1400, "webp",
            (long)(0.47 * 1400 * 1500 / 8) * 20, null, 1500);
        var listing = new QualityCandidate(QualityTier.Aggregator, null, null, null, null, 31, null, null, null, null);

        var applied = SourceQualityEstimate.From([Sample(1400, 0.47, "webp"), Sample(1400, 0.47, "webp")])!.Apply(listing);

        Assert.Equal(31, applied.PageCount);
        Assert.Null(applied.SizeBytes);
        Assert.Equal(MeasuredQuality.Points(profile, measured), MeasuredQuality.Points(profile, applied));
    }
}
