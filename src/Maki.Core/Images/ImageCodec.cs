using System.Buffers.Binary;
using Maki.Core.Images.Upscaling;
using NetVips;
using VipsImage = NetVips.Image;

namespace Maki.Core.Images;

public enum ImageFormat
{
    Jpeg,
    Png,
    Webp,
    Gif,
    Avif,
    Bmp,
}

public enum TranscodeFormat
{
    Jpeg,
    Png
}

/// <summary>What a header says, read without decoding any pixels.</summary>
public readonly record struct ImageHeader(int Width, int Height, ImageFormat? Format);

/// <summary>
/// Every libvips call in Maki goes through here or <see cref="Raster"/>, so the process-wide setup in
/// the static constructor is guaranteed to run before the first image is opened.
/// </summary>
public static class ImageCodec
{
    /// <summary>
    /// The only loaders allowed to touch input. Pages and covers come from scraped sites and user
    /// uploads, so every other libvips loader (TIFF, SVG, PDF, FITS, matrix, ...) stays blocked:
    /// a format Maki never needed is attack surface and nothing else.
    /// </summary>
    private static readonly string[] AllowedLoaders =
    [
        "VipsForeignLoadJpeg",
        "VipsForeignLoadPng",
        "VipsForeignLoadWebp",
        "VipsForeignLoadNsgif",
        "VipsForeignLoadHeif",
    ];

    static ImageCodec()
    {
        // The operation cache keeps recent inputs alive, buffers and open files alike. Maki never
        // repeats an operation on the same input, and a file handle held past dispose blocks
        // File.Move on Windows.
        Cache.Max = 0;
        Cache.MaxMem = 0;
        Cache.MaxFiles = 0;

        global::NetVips.NetVips.BlockUntrusted = true;
        Operation.Block("VipsForeignLoad", true);
        foreach (var loader in AllowedLoaders)
        {
            Operation.Block(loader, false);
        }
    }

    /// <summary>Reads width, height and format from the header. Throws when the bytes are not an image a permitted loader accepts.</summary>
    public static ImageHeader Identify(byte[] bytes)
    {
        if (TryBmp(bytes, out var bmp)) return bmp;
        using var image = VipsImage.NewFromBuffer(bytes, access: Enums.Access.Sequential);
        return Header(image, bytes);
    }

    /// <inheritdoc cref="Identify(byte[])"/>
    public static ImageHeader Identify(ReadOnlySpan<byte> bytes) => Identify(bytes.ToArray());

    /// <inheritdoc cref="Identify(byte[])"/>
    public static ImageHeader IdentifyFile(string path)
    {
        var start = new byte[BmpHeaderBytes];
        using (var stream = File.OpenRead(path))
        {
            var read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
            if (TryBmp(start.AsSpan(0, read), out var bmp)) return bmp;
        }

        using var image = VipsImage.NewFromFile(path, access: Enums.Access.Sequential);
        return Header(image, null);
    }

    public static ImageHeader? TryIdentify(byte[] bytes)
    {
        try
        {
            return Identify(bytes);
        }
        catch (VipsException)
        {
            return null;
        }
    }

    private const int BmpHeaderBytes = 26;

    /// <summary>
    /// libvips reads BMP only through ImageMagick, which this build leaves out. Maki still accepts
    /// .bmp pages (the reader serves their bytes as they are), so the header is read here to keep a
    /// BMP page from looking damaged to the health scan and the quality measure.
    /// </summary>
    private static bool TryBmp(ReadOnlySpan<byte> bytes, out ImageHeader header)
    {
        header = default;
        if (bytes.Length < BmpHeaderBytes || bytes[0] != 'B' || bytes[1] != 'M') return false;
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        int width, height;
        if (dibSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
        }
        else if (dibSize >= 40)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
            // Negative means rows are stored top-down; the size is the same.
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]));
        }
        else return false;

        if (width <= 0 || height <= 0) return false;
        header = new ImageHeader(width, height, ImageFormat.Bmp);
        return true;
    }

    /// <summary>The format a permitted loader recognises from the signature, or null.</summary>
    public static ImageFormat? DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (TryBmp(bytes, out _)) return ImageFormat.Bmp;
        var loader = VipsImage.FindLoadBuffer(bytes.ToArray());
        return loader is null ? null : FormatOf(loader, bytes);
    }

    /// <summary>
    /// A JPEG at most <paramref name="width"/> wide, shrunk while decoding where the format allows
    /// it (a JPEG scales in the DCT, so a 2000x3000 scan never exists as full-size pixels). Only the
    /// first frame of an animation is used, EXIF orientation is applied, colour is converted to
    /// sRGB, transparency lands on white and every metadata block is dropped.
    /// <paramref name="enlarge"/> also scales a narrower image up to the width.
    /// </summary>
    public static byte[] JpegThumbnail(byte[] source, int width, int quality, bool enlarge)
    {
        using var thumb = VipsImage.ThumbnailBuffer(source, width, height: 10_000_000,
            size: enlarge ? Enums.Size.Both : Enums.Size.Down, outputProfile: "srgb");
        return SaveJpeg(thumb, quality);
    }

    /// <summary>
    /// The whole image re-encoded for a client that cannot decode its format: same size, first frame
    /// of an animation, orientation applied, sRGB. A PNG keeps transparency; a JPEG puts it on white.
    /// Throws <see cref="VipsException"/> when no permitted loader reads the bytes.
    /// </summary>
    public static byte[] Transcode(byte[] source, TranscodeFormat format)
    {
        using var image = VipsImage.ThumbnailBuffer(source, MaxTranscodeEdge, height: MaxTranscodeEdge,
            size: Enums.Size.Down, outputProfile: "srgb");
        return format == TranscodeFormat.Png
            ? image.PngsaveBuffer(keep: Enums.ForeignKeep.None)
            : SaveJpeg(image, 90);
    }

    /// <summary>Large enough that <see cref="Transcode"/> never shrinks a page, even a long strip.</summary>
    private const int MaxTranscodeEdge = 1 << 20;

    /// <summary>
    /// Saves 4:2:0 regardless of quality. libvips' own default drops chroma subsampling at 90 and
    /// above, which makes a cover about a third larger than Maki ever wrote it.
    /// </summary>
    internal static byte[] SaveJpeg(VipsImage image, int quality)
    {
        if (image.HasAlpha())
        {
            using var flat = image.Flatten(background: [255, 255, 255]);
            return flat.JpegsaveBuffer(q: quality, subsampleMode: Enums.ForeignSubsample.On, keep: Enums.ForeignKeep.None);
        }

        return image.JpegsaveBuffer(q: quality, subsampleMode: Enums.ForeignSubsample.On, keep: Enums.ForeignKeep.None);
    }

    /// <summary>
    /// Decodes a JPEG or PNG page and encodes it as <paramref name="target"/>, or returns null with
    /// the reason it was left as it is. Orientation is applied, CMYK is converted to sRGB and an ICC
    /// profile is carried over; everything else in the metadata is dropped. Throws
    /// <see cref="VipsException"/> when the bytes do not decode cleanly: libvips would otherwise fill
    /// a truncated JPEG with grey and the damage would be baked into a page that looks whole.
    /// </summary>
    public static byte[]? EncodePage(byte[] source, PageEncoding target, int quality, out PageSkip? skipped)
    {
        skipped = null;
        using var loaded = VipsImage.NewFromBuffer(source, failOn: Enums.FailOn.Error);
        if (loaded.Format != Enums.BandFormat.Uchar)
        {
            skipped = PageSkip.HighBitDepth;
            return null;
        }

        if (Math.Max(loaded.Width, loaded.Height) > PageConversion.MaxEdge ||
            (long)loaded.Width * loaded.Height > Reading.ArchiveHealthAnalyzer.MaxPixels)
        {
            skipped = PageSkip.TooLarge;
            return null;
        }

        using var rotated = loaded.Autorot();
        using var image = rotated.Interpretation == Enums.Interpretation.Cmyk
            ? rotated.Colourspace(Enums.Interpretation.Srgb)
            : rotated.Copy();
        return target switch
        {
            PageEncoding.LosslessWebp => image.WebpsaveBuffer(lossless: true, effort: 4, keep: Enums.ForeignKeep.Icc),
            PageEncoding.Webp => image.WebpsaveBuffer(q: quality, effort: 4, smartSubsample: true, keep: Enums.ForeignKeep.Icc),
            PageEncoding.Avif => image.HeifsaveBuffer(q: quality, bitdepth: 8, compression: Enums.ForeignHeifCompression.Av1,
                effort: 4, keep: Enums.ForeignKeep.Icc),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
    }

    /// <summary>
    /// Decodes a page for an upscale model, or returns a reason it cannot be: the bytes do not decode
    /// cleanly (<see cref="UpscaleReasons.Unreadable"/>), more than 8 bits per channel, or over
    /// <see cref="PageConversion.MaxEdge"/> or <see cref="Reading.ArchiveHealthAnalyzer.MaxPixels"/>.
    /// Orientation is applied, CMYK and greyscale become sRGB, transparency lands on white, and the
    /// result always has 3 bands.
    /// </summary>
    public static (Raster? Raster, string? SkipReason) DecodeForUpscale(byte[] source)
    {
        try
        {
            using var loaded = VipsImage.NewFromBuffer(source, failOn: Enums.FailOn.Error);
            if (loaded.Format != Enums.BandFormat.Uchar)
            {
                return (null, UpscaleReasons.HighBitDepth);
            }

            if (Math.Max(loaded.Width, loaded.Height) > PageConversion.MaxEdge ||
                (long)loaded.Width * loaded.Height > Reading.ArchiveHealthAnalyzer.MaxPixels)
            {
                return (null, UpscaleReasons.TooLarge);
            }

            using var rotated = loaded.Autorot();
            using var flat = rotated.HasAlpha() ? rotated.Flatten(background: [255, 255, 255]) : rotated.Copy();
            using var srgb = flat.Interpretation == Enums.Interpretation.Cmyk
                ? flat.Colourspace(Enums.Interpretation.Srgb)
                : flat.Copy();
            using var rgb = srgb.Bands switch
            {
                1 => srgb.Bandjoin(srgb, srgb),
                3 => srgb.Copy(),
                _ => srgb.ExtractBand(0, n: 3)
            };
            using var bytes = rgb.Format == Enums.BandFormat.Uchar ? rgb.Copy() : rgb.Cast(Enums.BandFormat.Uchar);
            return (Raster.Wrap(bytes.Width, bytes.Height, 3, bytes.WriteToMemory<byte>()), null);
        }
        catch (VipsException)
        {
            return (null, UpscaleReasons.Unreadable);
        }
    }

    /// <summary>
    /// Resizes interleaved 8-bit RGB by <paramref name="scale"/> with Lanczos3, for a 4× model whose
    /// output the reader gets at 2×.
    /// </summary>
    public static (byte[] Rgb, int Width, int Height) ResizeRgb(byte[] rgb, int width, int height, double scale)
    {
        using var image = FromMemory(rgb, width, height, 3);
        using var resized = image.Resize(scale, kernel: Enums.Kernel.Lanczos3);
        return (resized.WriteToMemory<byte>(), resized.Width, resized.Height);
    }

    /// <summary>WebP's hard limit on either edge; an upscaled page past it is saved as JPEG.</summary>
    public const int MaxWebpEdge = 16383;

    /// <summary>
    /// Encodes an upscaled page from interleaved 8-bit RGB: WebP q85, or JPEG q90 when either edge is
    /// over <see cref="MaxWebpEdge"/>. A grey page (<see cref="UpscalePixels.IsGreyscale"/>) is saved
    /// with one band.
    /// </summary>
    public static UpscaledImage EncodeUpscaled(byte[] rgb, int width, int height)
    {
        var grey = UpscalePixels.IsGreyscale(rgb);
        var pixels = grey ? UpscalePixels.ToSingleBand(rgb) : rgb;
        var bands = grey ? 1 : 3;
        using var raw = FromMemory(pixels, width, height, bands);
        using var image = raw.Copy(interpretation: grey ? Enums.Interpretation.Bw : Enums.Interpretation.Srgb);
        return Math.Max(width, height) > MaxWebpEdge
            ? new UpscaledImage(image.JpegsaveBuffer(q: 90, subsampleMode: Enums.ForeignSubsample.On,
                keep: Enums.ForeignKeep.None), "jpg", width, height, bands)
            : new UpscaledImage(image.WebpsaveBuffer(q: 85, effort: 4, smartSubsample: !grey,
                keep: Enums.ForeignKeep.None), "webp", width, height, bands);
    }

    internal static VipsImage Open(byte[] bytes) => VipsImage.NewFromBuffer(bytes, access: Enums.Access.Sequential);

    internal static VipsImage ThumbnailFile(string path, int width, int height) =>
        VipsImage.Thumbnail(path, width, height: height, size: Enums.Size.Force, noRotate: true);

    internal static VipsImage FromMemory(byte[] pixels, int width, int height, int bands) =>
        VipsImage.NewFromMemory(pixels, width, height, bands, Enums.BandFormat.Uchar);

    private static ImageHeader Header(VipsImage image, byte[]? bytes)
    {
        var loader = image.Contains("vips-loader") ? image.Get("vips-loader") as string : null;
        return new ImageHeader(image.Width, image.Height, loader is null ? null : FormatOf(loader, bytes));
    }

    /// <summary>
    /// A loaded image names its loader by nickname (<c>webpload_buffer</c>), the loader search by
    /// class (<c>VipsForeignLoadWebpBuffer</c>), so both spellings are matched.
    /// </summary>
    private static ImageFormat? FormatOf(string loader, ReadOnlySpan<byte> bytes)
    {
        var name = loader.ToLowerInvariant();
        if (name.Contains("jpeg")) return ImageFormat.Jpeg;
        if (name.Contains("png")) return ImageFormat.Png;
        if (name.Contains("webp")) return ImageFormat.Webp;
        if (name.Contains("gif")) return ImageFormat.Gif;
        // The HEIF loader also opens HEIC, which is not a format Maki serves; the brand tells them
        // apart. From a file there are no bytes to check, and Maki writes no HEIF files of its own.
        if (name.Contains("heif") &&
            (bytes.IsEmpty || Download.ImageValidator.SniffMediaType(bytes) == "image/avif"))
            return ImageFormat.Avif;
        return null;
    }
}
