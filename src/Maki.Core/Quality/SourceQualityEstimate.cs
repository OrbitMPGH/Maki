using Maki.Core.Entities;

namespace Maki.Core.Quality;

/// <summary>
/// What a source's next chapter of a series will probably measure, from the medians of its recent
/// samples. Width and compression barely move between chapters of one series on one source, so a
/// handful of samples predicts the next copy well enough to decide whether probing it is worth it.
/// </summary>
/// <param name="BitsPerPixel">JPG-equivalent, each sample adjusted for its own format before the median, so a source mixing PNG and JPG chapters is not judged by whichever format is commoner.</param>
public sealed record SourceQualityEstimate(
    int Samples, int MedianWidth, int MedianHeight, double BitsPerPixel, string? ImageFormat, DateTime LatestUtc)
{
    public const int MinSamples = 2;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

    /// <summary>Enough recent samples to stand in for a probe.</summary>
    public bool IsReliable(DateTime nowUtc) => Samples >= MinSamples && nowUtc - LatestUtc <= MaxAge;

    public static SourceQualityEstimate? From(IReadOnlyCollection<SourceQualitySample> samples)
    {
        var usable = samples.Where(s => s is { PageCount: > 0, MedianWidth: > 0, MedianHeight: > 0, SizeBytes: > 0 }).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        var format = usable
            .GroupBy(s => s.ImageFormat ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(s => s.MeasuredAtUtc))
            .First().Key;
        return new SourceQualityEstimate(
            usable.Count,
            (int)Median(usable.Select(s => (double)s.MedianWidth)),
            (int)Median(usable.Select(s => (double)s.MedianHeight)),
            Median(usable.Select(s => s.SizeBytes * 8.0 / s.PageCount / ((double)s.MedianWidth * s.MedianHeight)
                                      * MeasuredQuality.FormatEfficiency(s.ImageFormat))),
            format.Length == 0 ? null : format,
            usable.Max(s => s.MeasuredAtUtc));
    }

    /// <summary><paramref name="listing"/> with this estimate's measurements; page count stays the listing's own.</summary>
    public QualityCandidate Apply(QualityCandidate listing) => listing with
    {
        MedianWidth = MedianWidth,
        MedianHeight = MedianHeight,
        ImageFormat = ImageFormat,
        EstimatedBitsPerPixel = BitsPerPixel
    };

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
