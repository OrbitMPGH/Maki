using System.Globalization;
using Maki.Core.Download;
using Maki.Core.Quality;
using NetVips;

namespace Maki.Core.Images;

/// <summary>What stored pages are converted to. JPEG XL is absent because the bundled libvips has no libjxl.</summary>
public enum PageConversionFormat
{
    Webp,
    Avif
}

/// <summary>How one page is written: lossy in the chosen format, or lossless WebP for a PNG.</summary>
public enum PageEncoding
{
    Webp,
    LosslessWebp,
    Avif
}

/// <summary>Why a page was copied over as it was.</summary>
public enum PageSkip
{
    TooSmall,
    AlreadyCompact,
    Animated,
    HighBitDepth,
    TooLarge,
    NotSmaller,

    /// <summary>A JPEG under <see cref="PageConversionStop.Lossless"/>: re-encoding it can only lose detail.</summary>
    Lossless
}

/// <summary>
/// The quality steps, each calibrated per format against a SSIMULACRA2 target: Smallest about 72,
/// Compact 79, Balanced 84, High 91 (visually lossless). Lossless keeps every pixel.
/// </summary>
public enum PageConversionStop
{
    Smallest,
    Compact,
    Balanced,
    High,
    Lossless
}

/// <param name="PngLossless">PNG pages go to lossless WebP whatever the stop.</param>
public sealed record PageConversionSettings(PageConversionFormat Format, PageConversionStop Stop, bool PngLossless)
{
    public static PageConversionStop DefaultStop => PageConversionStop.High;

    private static readonly PageConversionStop[] LossyStops =
        [PageConversionStop.Smallest, PageConversionStop.Compact, PageConversionStop.Balanced, PageConversionStop.High];

    /// <summary>The encoder quality a lossy stop stands for. Lossless has none.</summary>
    public static int Quality(PageConversionFormat format, PageConversionStop stop) => (format, stop) switch
    {
        (PageConversionFormat.Webp, PageConversionStop.Smallest) => 50,
        (PageConversionFormat.Webp, PageConversionStop.Compact) => 70,
        (PageConversionFormat.Webp, PageConversionStop.Balanced) => 80,
        (PageConversionFormat.Webp, PageConversionStop.High) => 90,
        (PageConversionFormat.Avif, PageConversionStop.Smallest) => 50,
        (PageConversionFormat.Avif, PageConversionStop.Compact) => 60,
        (PageConversionFormat.Avif, PageConversionStop.Balanced) => 65,
        (PageConversionFormat.Avif, PageConversionStop.High) => 80,
        _ => throw new ArgumentOutOfRangeException(nameof(stop), stop, null)
    };

    /// <summary>The lossy stop closest to a 1 to 100 quality; a tie goes to the higher-quality stop.</summary>
    public static PageConversionStop NearestStop(PageConversionFormat format, int quality)
    {
        var nearest = LossyStops[0];
        foreach (var stop in LossyStops)
        {
            if (Math.Abs(Quality(format, stop) - quality) <= Math.Abs(Quality(format, nearest) - quality))
            {
                nearest = stop;
            }
        }

        return nearest;
    }

    /// <summary>"webp:high", "avif:balanced+png" or "lossless".</summary>
    public string Name() => Stop == PageConversionStop.Lossless
        ? StopName(Stop)
        : $"{FormatName(Format)}:{StopName(Stop)}{(PngLossless ? "+png" : "")}";

    public static PageConversionSettings? Parse(string? name)
    {
        var value = name?.Trim().ToLowerInvariant();
        if (value == StopName(PageConversionStop.Lossless))
        {
            return new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.Lossless, false);
        }

        if (value is null)
        {
            return null;
        }

        var png = value.EndsWith("+png", StringComparison.Ordinal);
        var parts = (png ? value[..^4] : value).Split(':');
        return parts.Length == 2 && ParseFormat(parts[0]) is { } format && ParseStop(parts[1]) is { } stop &&
               stop != PageConversionStop.Lossless
            ? new PageConversionSettings(format, stop, png)
            : null;
    }

    public static string FormatName(PageConversionFormat format) => format == PageConversionFormat.Avif ? "avif" : "webp";

    public static PageConversionFormat? ParseFormat(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "webp" => PageConversionFormat.Webp,
        "avif" => PageConversionFormat.Avif,
        _ => null
    };

    public static string StopName(PageConversionStop stop) => stop.ToString().ToLowerInvariant();

    public static PageConversionStop? ParseStop(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "smallest" => PageConversionStop.Smallest,
        "compact" => PageConversionStop.Compact,
        "balanced" => PageConversionStop.Balanced,
        "high" => PageConversionStop.High,
        "lossless" => PageConversionStop.Lossless,
        _ => null
    };
}

/// <summary>
/// The per-page rules. Only JPEG and PNG are converted: WebP and AVIF pages are already compact and a
/// second lossy pass only loses detail, GIF and APNG may be animated, and BMP has no decoder here.
/// At <see cref="PageConversionStop.Lossless"/> a JPEG is kept and a PNG goes to lossless WebP
/// (lossless AVIF is usually larger than the PNG). At a lossy stop both are encoded in the chosen
/// format, unless <see cref="PageConversionSettings.PngLossless"/> keeps PNG pages lossless.
/// </summary>
public static class PageConversion
{
    /// <summary>Credits, separators and blank pages: too small for the saving to be worth a decode.</summary>
    public const int MinPageBytes = 16 * 1024;

    /// <summary>WebP's hard limit, applied to AVIF too so a long strip gets one rule.</summary>
    public const int MaxEdge = 16383;

    /// <summary>A converted page has to come out at least this much smaller, or the original is kept.</summary>
    public const double MinPageSaving = 0.05;

    /// <summary>A file is replaced only when the whole archive shrinks by at least this much.</summary>
    public const double MinFileSaving = 0.05;

    public readonly record struct Page(byte[] Bytes, string Extension, string SourceFormat, PageSkip? Skipped)
    {
        public bool Converted => Skipped is null;
    }

    /// <summary>
    /// The page to store. Returns the original bytes and extension with a reason when it is kept.
    /// Throws <see cref="VipsException"/> when a JPEG or PNG does not decode, so a damaged file is
    /// left for the health scan instead of being rewritten around the damage.
    /// </summary>
    public static Page Convert(byte[] bytes, string extension, PageConversionSettings settings)
    {
        var format = FormatOf(bytes, extension);
        if (format is not ("jpg" or "png"))
        {
            return new Page(bytes, extension, format, PageSkip.AlreadyCompact);
        }

        if (format == "jpg" && settings.Stop == PageConversionStop.Lossless)
        {
            return new Page(bytes, extension, format, PageSkip.Lossless);
        }

        if (bytes.Length < MinPageBytes)
        {
            return new Page(bytes, extension, format, PageSkip.TooSmall);
        }

        if (format == "png" && IsAnimatedPng(bytes))
        {
            return new Page(bytes, extension, format, PageSkip.Animated);
        }

        var lossless = settings.Stop == PageConversionStop.Lossless;
        var encoding = format == "png" && (lossless || settings.PngLossless)
            ? PageEncoding.LosslessWebp
            : settings.Format == PageConversionFormat.Avif ? PageEncoding.Avif : PageEncoding.Webp;
        var quality = encoding == PageEncoding.LosslessWebp ? 100 : PageConversionSettings.Quality(settings.Format, settings.Stop);
        var encoded = ImageCodec.EncodePage(bytes, encoding, quality, out var skipped);
        if (encoded is null)
        {
            return new Page(bytes, extension, format, skipped);
        }

        if (encoded.Length > bytes.Length * (1 - MinPageSaving))
        {
            return new Page(bytes, extension, format, PageSkip.NotSmaller);
        }

        return new Page(encoded, encoding == PageEncoding.Avif ? ".avif" : ".webp", format, null);
    }

    /// <summary>The format name <see cref="ChapterFileMeasurer"/> would give these bytes.</summary>
    public static string FormatOf(byte[] bytes, string extension)
    {
        var sniffed = ImageValidator.SniffMediaType(bytes) switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/avif" => "avif",
            _ => null
        };
        if (sniffed is not null) return sniffed;
        if (bytes.Length > 1 && bytes[0] == 'B' && bytes[1] == 'M') return "bmp";
        return extension.TrimStart('.').ToLower(CultureInfo.InvariantCulture) switch
        {
            "jpeg" => "jpg",
            var other => other
        };
    }

    /// <summary>An APNG declares its animation in an acTL chunk ahead of the first IDAT.</summary>
    internal static bool IsAnimatedPng(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        while (offset + 8 <= bytes.Length)
        {
            var length = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]), int.MaxValue);
            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8)) return true;
            if (type.SequenceEqual("IDAT"u8)) return false;
            if (length > bytes.Length - offset - 12) return false;
            offset += 12 + length;
        }

        return false;
    }

    /// <summary>One format name for a set of pages, the way <see cref="ChapterFileMeasurer"/> reports it.</summary>
    public static string ResolveFormat(IEnumerable<string> formats)
    {
        var distinct = formats.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count switch
        {
            0 => "unknown",
            1 => distinct[0],
            _ => "mixed"
        };
    }
}
