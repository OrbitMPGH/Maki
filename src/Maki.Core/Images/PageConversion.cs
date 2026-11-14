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
    Lossless,

    /// <summary>The plan only splits tall pages in this file, and this one is not.</summary>
    SplitOnly
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

/// <summary>How one kind of page is converted: a format at a stop. A Lossless target is always lossless WebP.</summary>
public sealed record PageTarget(PageConversionFormat Format, PageConversionStop Stop)
{
    public PageTarget Normalised() =>
        Stop == PageConversionStop.Lossless ? this with { Format = PageConversionFormat.Webp } : this;

    /// <summary>"webp:high" or "lossless".</summary>
    public string Name() => Stop == PageConversionStop.Lossless
        ? PageConversionSettings.StopName(Stop)
        : $"{PageConversionSettings.FormatName(Format)}:{PageConversionSettings.StopName(Stop)}";

    public static PageTarget? Parse(string? name)
    {
        var value = name?.Trim().ToLowerInvariant();
        if (value == PageConversionSettings.StopName(PageConversionStop.Lossless))
        {
            return new PageTarget(PageConversionFormat.Webp, PageConversionStop.Lossless);
        }

        var parts = value?.Split(':');
        return parts is { Length: 2 } && PageConversionSettings.ParseFormat(parts[0]) is { } format &&
               PageConversionSettings.ParseStop(parts[1]) is { } stop && stop != PageConversionStop.Lossless
            ? new PageTarget(format, stop)
            : null;
    }
}

/// <param name="Format">The format of black-and-white pages, and of colour pages while <paramref name="Colour"/> is unset.</param>
/// <param name="Stop">The stop of black-and-white pages, and of colour pages while <paramref name="Colour"/> is unset.</param>
/// <param name="PngLossless">PNG pages go to lossless WebP whatever the stop.</param>
/// <param name="SplitHeight">Strips taller than this are cut into several pages (<see cref="TallPageSplitter"/>); null leaves them whole.</param>
/// <param name="SplitOnly">
/// Only split: every page that is not a tall strip is kept as it is. For files already stored as WebP or
/// AVIF, and files converted before. The targets still set the quality of a WebP or AVIF piece.
/// </param>
/// <param name="Colour">How colour pages are converted; null converts them like black-and-white pages.</param>
public sealed record PageConversionSettings(
    PageConversionFormat Format, PageConversionStop Stop, bool PngLossless, int? SplitHeight = null, bool SplitOnly = false,
    PageTarget? Colour = null)
{
    public PageTarget Grey => new PageTarget(Format, Stop).Normalised();

    public PageTarget ColourTarget => (Colour ?? Grey).Normalised();

    /// <summary>Colour and black-and-white pages are converted differently, so each page is checked for colour first.</summary>
    public bool ByColour => ColourTarget != Grey;

    public PageTarget Target(bool colour) => colour ? ColourTarget : Grey;

    /// <summary>Every format a page of this file may be written in.</summary>
    public bool UsesFormat(PageConversionFormat format) =>
        (Grey.Stop != PageConversionStop.Lossless && Grey.Format == format) ||
        (ColourTarget.Stop != PageConversionStop.Lossless && ColourTarget.Format == format);

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

    /// <summary>
    /// "webp:high", "avif:balanced+png", "lossless", "webp:high+split5000" or, split only, "split5000".
    /// Colour pages that convert differently follow a slash: "webp:high/avif:balanced+png".
    /// </summary>
    public string Name()
    {
        var split = SplitHeight is { } height ? SplitName(height) : null;
        if (SplitOnly && split is not null)
        {
            return split;
        }

        var grey = Grey;
        var colour = ColourTarget;
        var lossy = grey.Stop != PageConversionStop.Lossless || (ByColour && colour.Stop != PageConversionStop.Lossless);
        var name = (ByColour ? $"{grey.Name()}/{colour.Name()}" : grey.Name()) + (lossy && PngLossless ? "+png" : "");
        return split is null ? name : $"{name}+{split}";
    }

    public static string SplitName(int height) => $"split{height.ToString(CultureInfo.InvariantCulture)}";

    public static PageConversionSettings? Parse(string? name)
    {
        var value = name?.Trim().ToLowerInvariant();
        if (value is null)
        {
            return null;
        }

        int? split = null;
        var plus = value.LastIndexOf('+');
        var last = plus < 0 ? value : value[(plus + 1)..];
        if (last.StartsWith("split", StringComparison.Ordinal))
        {
            if (!int.TryParse(last.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height <= 0)
            {
                return null;
            }

            split = height;
            if (plus < 0)
            {
                return new PageConversionSettings(PageConversionFormat.Webp, DefaultStop, false, split, SplitOnly: true);
            }

            value = value[..plus];
        }

        var png = value.EndsWith("+png", StringComparison.Ordinal);
        var targets = (png ? value[..^4] : value).Split('/');
        if (targets.Length is not (1 or 2) || PageTarget.Parse(targets[0]) is not { } grey)
        {
            return null;
        }

        PageTarget? colour = null;
        if (targets.Length == 2 && (PageTarget.Parse(targets[1]) is not { } parsed || (colour = parsed) == grey))
        {
            return null;
        }

        var lossy = grey.Stop != PageConversionStop.Lossless || colour is { Stop: not PageConversionStop.Lossless };
        if (png && !lossy)
        {
            return null;
        }

        return new PageConversionSettings(grey.Format, grey.Stop, png, split, Colour: colour);
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

    /// <param name="Colour">The page was checked for colour and found in colour; false when it was grey or never checked.</param>
    public readonly record struct Page(byte[] Bytes, string Extension, string SourceFormat, PageSkip? Skipped, bool Colour = false)
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
        var colour = settings.ByColour && Convertible(bytes, extension) && ImageCodec.IsColour(bytes) == true;
        var target = settings.Target(colour);
        if (Decide(bytes, extension, target, settings.PngLossless, out var format, out var kept) is not { } encoding)
        {
            return new Page(bytes, extension, format, kept, colour);
        }

        var quality = encoding == PageEncoding.LosslessWebp ? 100 : PageConversionSettings.Quality(target.Format, target.Stop);
        var encoded = ImageCodec.EncodePage(bytes, encoding, quality, out var skipped);
        if (encoded is null)
        {
            return new Page(bytes, extension, format, skipped, colour);
        }

        if (encoded.Length > bytes.Length * (1 - MinPageSaving))
        {
            return new Page(bytes, extension, format, PageSkip.NotSmaller, colour);
        }

        return new Page(encoded, ExtensionOf(encoding), format, null, colour);
    }

    /// <summary>
    /// The page or pages to store in its place: the pieces of a tall strip when the settings split, else
    /// what <see cref="Convert"/> answers (or the page as it is, when the settings only split).
    /// </summary>
    public static IReadOnlyList<Page> ConvertPage(byte[] bytes, string extension, PageConversionSettings settings)
    {
        if (TallPageSplitter.Split(bytes, extension, settings) is { } pieces)
        {
            return pieces;
        }

        return settings.SplitOnly
            ? [new Page(bytes, extension, FormatOf(bytes, extension), PageSkip.SplitOnly)]
            : [Convert(bytes, extension, settings)];
    }

    /// <summary>
    /// How <see cref="Convert"/> encodes the page when it is in colour or not, or null when it keeps the
    /// page without decoding it.
    /// </summary>
    public static PageEncoding? EncodingFor(byte[] bytes, string extension, PageConversionSettings settings, bool colour) =>
        Decide(bytes, extension, settings.Target(colour), settings.PngLossless, out _, out _);

    /// <summary>
    /// What encoding the page takes from the conversion gate, read from its header before any decode.
    /// When colour and black-and-white pages convert differently the heavier of the two is taken, since
    /// which one applies is only known once the page is decoded.
    /// </summary>
    public static ConversionWeight WeightOf(byte[] bytes, string extension, PageConversionSettings settings)
    {
        if (ImageCodec.TryShape(bytes) is not { } shape)
        {
            return ImageWorkGate.LightConversion;
        }

        ConversionWeight? Weigh(bool colour)
        {
            if (settings.SplitHeight is { } height && TallPageSplitter.IsTall(shape.Width, shape.Height, height))
            {
                return ImageWorkGate.WeighConversion(shape.Width, shape.Height, shape.Bands,
                    TallPageSplitter.WeightEncoding(bytes, extension, settings, colour));
            }

            return !settings.SplitOnly && EncodingFor(bytes, extension, settings, colour) is { } encoding
                ? ImageWorkGate.WeighConversion(shape.Width, shape.Height, shape.Bands, encoding)
                : null;
        }

        var grey = Weigh(false);
        var colour = settings.ByColour && shape.Bands >= 3 ? Weigh(true) : grey;
        return (grey, colour) switch
        {
            (null, null) => ImageWorkGate.LightConversion,
            ({ } g, null) => g,
            (null, { } c) => c,
            ({ } g, { } c) => new ConversionWeight(Math.Max(g.Megabytes, c.Megabytes), g.Avif || c.Avif)
        };
    }

    public static string ExtensionOf(PageEncoding encoding) => encoding == PageEncoding.Avif ? ".avif" : ".webp";

    /// <summary>A JPEG or PNG that <see cref="Decide"/> would not keep on sight, whatever the target.</summary>
    private static bool Convertible(byte[] bytes, string extension) =>
        FormatOf(bytes, extension) is var format && (format == "jpg" || (format == "png" && !IsAnimatedPng(bytes))) &&
        bytes.Length >= MinPageBytes;

    private static PageEncoding? Decide(byte[] bytes, string extension, PageTarget target, bool pngLossless,
        out string format, out PageSkip? kept)
    {
        format = FormatOf(bytes, extension);
        kept = format is not ("jpg" or "png") ? PageSkip.AlreadyCompact
            : format == "jpg" && target.Stop == PageConversionStop.Lossless ? PageSkip.Lossless
            : bytes.Length < MinPageBytes ? PageSkip.TooSmall
            : format == "png" && IsAnimatedPng(bytes) ? PageSkip.Animated
            : null;
        if (kept is not null)
        {
            return null;
        }

        return format == "png" && (target.Stop == PageConversionStop.Lossless || pngLossless)
            ? PageEncoding.LosslessWebp
            : target.Format == PageConversionFormat.Avif ? PageEncoding.Avif : PageEncoding.Webp;
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
