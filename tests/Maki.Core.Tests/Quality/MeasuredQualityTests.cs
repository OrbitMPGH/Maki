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

    [Fact]
    public void Resolution_is_points_per_doubling_of_width()
    {
        Assert.Equal(10, MeasuredQuality.Points(Profile(), Candidate(width: MeasuredQuality.ReferenceWidth * 2)).Resolution);
        Assert.Equal(20, MeasuredQuality.Points(Profile(20, 10), Candidate(width: MeasuredQuality.ReferenceWidth * 2)).Resolution);

        // Log scale, not linear: a linear scale would give 4 here.
        Assert.Equal(5, MeasuredQuality.Points(Profile(), Candidate(width: (int)(MeasuredQuality.ReferenceWidth * Math.Sqrt(2)))).Resolution);
    }

    [Fact]
    public void A_wider_copy_never_scores_lower_on_resolution_and_the_ends_are_clamped()
    {
        var profile = Profile();
        var points = Enumerable.Range(1, 80).Select(i => i * 100)
            .Select(width => MeasuredQuality.Points(profile, Candidate(width: width)).Resolution)
            .ToList();

        Assert.Equal(points.Order(), points);
        Assert.True(points[0] < points[^1]);
        Assert.Equal(
            MeasuredQuality.Points(profile, Candidate(width: MeasuredQuality.MaxWidth)).Resolution,
            MeasuredQuality.Points(profile, Candidate(width: MeasuredQuality.MaxWidth * 3)).Resolution);
        Assert.Equal(
            MeasuredQuality.Points(profile, Candidate(width: MeasuredQuality.MinWidth)).Resolution,
            MeasuredQuality.Points(profile, Candidate(width: MeasuredQuality.MinWidth / 3)).Resolution);
    }

    [Fact]
    public void Compression_is_points_per_doubling_of_jpg_equivalent_bits_per_pixel()
    {
        Assert.Equal(10, MeasuredQuality.Points(Profile(), Candidate(bitsPerPixel: MeasuredQuality.ReferenceBitsPerPixel * 2)).Compression);
        Assert.Equal(-10, MeasuredQuality.Points(Profile(), Candidate(bitsPerPixel: MeasuredQuality.ReferenceBitsPerPixel / 2)).Compression);
    }

    [Fact]
    public void A_lighter_compressed_copy_never_scores_lower_and_the_ends_are_clamped()
    {
        var profile = Profile();
        var points = Enumerable.Range(1, 120).Select(i => i * 0.05)
            .Select(bpp => MeasuredQuality.Points(profile, Candidate(bitsPerPixel: bpp)).Compression)
            .ToList();

        Assert.Equal(points.Order(), points);
        Assert.True(points[0] < points[^1]);
        Assert.Equal(
            MeasuredQuality.Points(profile, Candidate(bitsPerPixel: MeasuredQuality.MaxBitsPerPixel)).Compression,
            MeasuredQuality.Points(profile, Candidate(bitsPerPixel: MeasuredQuality.MaxBitsPerPixel * 50)).Compression);
        Assert.Equal(
            MeasuredQuality.Points(profile, Candidate(bitsPerPixel: MeasuredQuality.MinBitsPerPixel)).Compression,
            MeasuredQuality.Points(profile, Candidate(bitsPerPixel: MeasuredQuality.MinBitsPerPixel / 50)).Compression);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("avif")]
    [InlineData("jpg")]
    [InlineData("mixed")]
    public void Bits_per_pixel_are_compared_after_converting_to_jpg_equivalent(string format)
    {
        var atReference = Candidate(bitsPerPixel: MeasuredQuality.ReferenceBitsPerPixel / MeasuredQuality.FormatEfficiency(format), format: format);

        Assert.Equal(0, MeasuredQuality.Points(Profile(), atReference).Compression);
    }

    [Fact]
    public void Png_is_discounted_and_webp_and_avif_are_credited_against_jpg()
    {
        Assert.True(MeasuredQuality.FormatEfficiency("png") < 1);
        Assert.True(MeasuredQuality.FormatEfficiency("webp") > 1);
        Assert.True(MeasuredQuality.FormatEfficiency("avif") >= MeasuredQuality.FormatEfficiency("webp"));
        Assert.Equal(1, MeasuredQuality.FormatEfficiency("jpg"));
        Assert.Equal(1, MeasuredQuality.FormatEfficiency("mixed"));
        Assert.Equal(1, MeasuredQuality.FormatEfficiency(null));
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
        var profile = Profile();
        var best = MeasuredQuality.Points(
            profile, Candidate(width: MeasuredQuality.MaxWidth, bitsPerPixel: MeasuredQuality.MaxBitsPerPixel));
        var narrow = MeasuredQuality.Points(profile, Candidate(width: MeasuredQuality.MinWidth, bitsPerPixel: null));

        Assert.Equal(best, MeasuredQuality.OptimisticPoints(profile, Candidate(width: null, bitsPerPixel: null)));
        Assert.Equal(
            (narrow.Resolution, best.Compression),
            MeasuredQuality.OptimisticPoints(profile, Candidate(width: MeasuredQuality.MinWidth, bitsPerPixel: null)));
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
