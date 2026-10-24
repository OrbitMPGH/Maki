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
    NotSmaller
}

public readonly record struct PageConversionSettings(PageConversionFormat Format, int Quality)
{
    public const int DefaultWebpQuality = 80;
    public const int DefaultAvifQuality = 55;

    public static int DefaultQuality(PageConversionFormat format) =>
        format == PageConversionFormat.Avif ? DefaultAvifQuality : DefaultWebpQuality;

    public string Extension => Format == PageConversionFormat.Avif ? ".avif" : ".webp";

    public static string Name(PageConversionFormat format) => format == PageConversionFormat.Avif ? "avif" : "webp";

    public static PageConversionFormat? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "webp" => PageConversionFormat.Webp,
        "avif" => PageConversionFormat.Avif,
        _ => null
    };
}

/// <summary>
/// The per-page rules. Only JPEG and PNG are converted: WebP and AVIF pages are already compact and a
/// second lossy pass only loses detail, GIF and APNG may be animated, and BMP has no decoder here.
/// A PNG goes to lossless WebP whatever the chosen format, because PNG pages are line art and screen
/// tones where lossy codecs smear the edges, and lossless AVIF is usually larger than the PNG.
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

        if (bytes.Length < MinPageBytes)
        {
            return new Page(bytes, extension, format, PageSkip.TooSmall);
        }

        if (format == "png" && IsAnimatedPng(bytes))
        {
            return new Page(bytes, extension, format, PageSkip.Animated);
        }

        var encoding = format == "png"
            ? PageEncoding.LosslessWebp
            : settings.Format == PageConversionFormat.Avif ? PageEncoding.Avif : PageEncoding.Webp;
        var encoded = ImageCodec.EncodePage(bytes, encoding, settings.Quality, out var skipped);
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
