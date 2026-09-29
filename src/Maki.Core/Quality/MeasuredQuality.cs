using Maki.Core.Entities;

namespace Maki.Core.Quality;

/// <summary>
/// Continuous points for how sharp and how lightly compressed a copy is, so a somewhat better copy
/// outscores a somewhat worse one without a format threshold in between. Each part is log2 of the
/// measurement against a reference, clamped, then multiplied by the profile's weight: the same
/// ratio is worth the same points anywhere on the scale.
/// </summary>
/// <remarks>
/// Constants come from sampling a real library of about 3,900 files. Median JPG page is 1.5 bits per
/// pixel (MangaDex 2.9, MangaFire 0.85); widths run 700 to 2700px. PNG is lossless and carries more
/// bits for the same picture: the same source's PNG pages sat at 0.52 to 0.73 of its JPG figure, so
/// PNG is scaled by 0.65. WebP and AVIF pack the same quality into fewer bits; their factors follow
/// published codec comparisons, since no source in the sample served both WebP and JPG.
/// </remarks>
public static class MeasuredQuality
{
    public const int ReferenceWidth = 1000;
    public const int MinWidth = 500;
    public const int MaxWidth = 2000;
    public const double ReferenceBitsPerPixel = 1.5;
    public const double MinBitsPerPixel = ReferenceBitsPerPixel / 4;
    public const double MaxBitsPerPixel = ReferenceBitsPerPixel * 4;

    public static double MaxResolutionUnits { get; } = Math.Log2((double)MaxWidth / ReferenceWidth);
    public static double MaxCompressionUnits { get; } = Math.Log2(MaxBitsPerPixel / ReferenceBitsPerPixel);

    /// <summary>JPG equivalent bits per pixel = measured bits per pixel times this.</summary>
    public static double FormatEfficiency(string? imageFormat) => imageFormat?.ToLowerInvariant() switch
    {
        "png" => 0.65,
        "webp" => 1.4,
        "avif" => 2.0,
        _ => 1.0
    };

    /// <summary>
    /// JPG-equivalent bits per pixel, measured from size, pages, width and height, else the candidate's
    /// estimate (already JPG-equivalent); null when neither is known.
    /// </summary>
    public static double? BitsPerPixel(QualityCandidate c) =>
        c is { SizeBytes: > 0 and var size, PageCount: > 0 and var pages, MedianWidth: > 0 and var width, MedianHeight: > 0 and var height }
            ? size * 8.0 / pages / ((double)width * height) * FormatEfficiency(c.ImageFormat)
            : c.EstimatedBitsPerPixel;

    public static double? ResolutionUnits(QualityCandidate c) =>
        c.MedianWidth is > 0 and var width
            ? Math.Log2((double)Math.Clamp(width, MinWidth, MaxWidth) / ReferenceWidth)
            : null;

    public static double? CompressionUnits(QualityCandidate c) =>
        BitsPerPixel(c) is { } bpp
            ? Math.Log2(Math.Clamp(bpp, MinBitsPerPixel, MaxBitsPerPixel) / ReferenceBitsPerPixel)
            : null;

    /// <summary>Unknown measurements score 0: not knowing is never evidence either way.</summary>
    public static (int Resolution, int Compression) Points(UpgradeProfile profile, QualityCandidate c) =>
        (Weigh(profile.ResolutionWeight, ResolutionUnits(c)), Weigh(profile.CompressionWeight, CompressionUnits(c)));

    /// <summary>Unknown measurements score the most they could, so a listing is never ruled out before it is probed.</summary>
    public static (int Resolution, int Compression) OptimisticPoints(UpgradeProfile profile, QualityCandidate c) =>
        (Weigh(profile.ResolutionWeight, ResolutionUnits(c) ?? MaxResolutionUnits),
         Weigh(profile.CompressionWeight, CompressionUnits(c) ?? MaxCompressionUnits));

    private static int Weigh(int weight, double? units) =>
        weight == 0 || units is null ? 0 : (int)Math.Round(weight * units.Value, MidpointRounding.AwayFromZero);
}
