using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Sources;

namespace Maki.Core.Tests.Quality;

public class MeasuredQualityTests
{
    private const int Height = 1500;
    private const int Pages = 20;

    private static QualityCandidate Candidate(
        int? width = 1000, double? bitsPerPixel = 1.5, string? format = "jpg", int? height = Height,
        QualityTier tier = QualityTier.Aggregator) =>
        new(tier, "mangafire", SourceKind.Aggregator, null, null, Pages, width, format,
            bitsPerPixel is { } bpp && width is { } w ? (long)(bpp * w * Height / 8) * Pages : null, "en", height);

    private static UpgradeProfile Profile(int resolution = 10, int compression = 10, int minScoreDelta = 5)
    {
        var profile = new UpgradeProfile
        {
            UpgradesEnabled = true, Cutoff = QualityTier.Official, MinScoreDelta = minScoreDelta,
            ResolutionWeight = resolution, CompressionWeight = compression
        };
        UpgradeProfileDefaults.Normalise(profile);
        return profile;
    }

    [Fact]
    public void The_reference_copy_scores_zero()
    {
        Assert.Equal((0, 0), MeasuredQuality.Points(Profile(), Candidate()));
    }

    [Theory]
    [InlineData(2000, 10)]
    [InlineData(1414, 5)]
    [InlineData(500, -10)]
    [InlineData(4000, 10)]
    [InlineData(200, -10)]
    public void Resolution_is_points_per_doubling_of_width_within_the_clamp(int width, int expected)
    {
        Assert.Equal(expected, MeasuredQuality.Points(Profile(), Candidate(width: width)).Resolution);
    }

    [Theory]
    [InlineData(3.0, "jpg", 10)]
    [InlineData(0.75, "jpg", -10)]
    [InlineData(100, "jpg", 20)]
    [InlineData(0.01, "jpg", -20)]
    [InlineData(1.5 / 0.65, "png", 0)]
    [InlineData(1.5 / 1.4, "webp", 0)]
    [InlineData(0.75, "avif", 0)]
    [InlineData(1.5, "mixed", 0)]
    public void Compression_is_points_per_doubling_of_jpg_equivalent_bits_per_pixel(
        double bitsPerPixel, string format, int expected)
    {
        Assert.Equal(expected,
            MeasuredQuality.Points(Profile(), Candidate(bitsPerPixel: bitsPerPixel, format: format)).Compression);
    }

    [Fact]
    public void Webtoon_slice_height_does_not_change_compression()
    {
        var shortSlices = Candidate(width: 800, bitsPerPixel: 2.13);
        var tallSlices = shortSlices with { MedianHeight = Height * 4, SizeBytes = shortSlices.SizeBytes * 4 };

        Assert.Equal(MeasuredQuality.CompressionUnits(shortSlices)!.Value, MeasuredQuality.CompressionUnits(tallSlices)!.Value, 6);
    }

    [Fact]
    public void Unknown_measurements_score_nothing_and_a_zero_weight_turns_a_part_off()
    {
        Assert.Equal((0, 0), MeasuredQuality.Points(Profile(), Candidate(width: null, bitsPerPixel: null)));
        Assert.Equal(0, MeasuredQuality.Points(Profile(), Candidate(height: null, bitsPerPixel: 6)).Compression);
        Assert.Equal((0, 0), MeasuredQuality.Points(Profile(0, 0), Candidate(width: 2000, bitsPerPixel: 6)));
    }

    [Fact]
    public void Optimistic_points_assume_the_best_only_for_what_is_unknown()
    {
        Assert.Equal((10, 20), MeasuredQuality.OptimisticPoints(Profile(), Candidate(width: null, bitsPerPixel: null)));
        Assert.Equal((-10, 20), MeasuredQuality.OptimisticPoints(Profile(), Candidate(width: 500, bitsPerPixel: null)));
    }

    [Fact]
    public void Score_carries_the_points_and_a_clearly_better_copy_of_the_same_tier_is_an_upgrade()
    {
        var profile = Profile();
        var current = QualityScorer.Score(profile, [], Candidate(width: 1400, bitsPerPixel: 0.85));
        var better = QualityScorer.Score(profile, [], Candidate(width: 1400, bitsPerPixel: 2.88));
        var slightlyBetter = QualityScorer.Score(profile, [], Candidate(width: 1500, bitsPerPixel: 1.0));

        Assert.Equal(current.ResolutionPoints + current.CompressionPoints, current.Score);
        Assert.True(QualityScorer.IsUpgrade(profile, current, Pages, false, better, 1400, Pages));
        Assert.False(QualityScorer.IsUpgrade(profile, current, Pages, false, slightlyBetter, 1500, Pages));
    }

    [Fact]
    public void A_listing_with_nothing_measured_is_still_worth_probing_against_a_same_tier_file()
    {
        var profile = Profile();
        var current = QualityScorer.Score(profile, [], Candidate(width: 1400, bitsPerPixel: 0.85));
        var listing = Candidate(width: null, bitsPerPixel: null);

        Assert.True(QualityScorer.CouldUpgrade(profile, [], current, Pages, false, listing));
        Assert.False(QualityScorer.CouldUpgrade(Profile(0, 0), [], QualityScorer.Score(Profile(0, 0), [], Candidate()), Pages, false, listing));
    }
}
