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

        // libvips otherwise starts one worker per core for every pipeline, on top of the parallelism
        // ImageWorkGate already allows, and each worker keeps its own buffers. On a 16-core host
        // that is four gated decodes times sixteen threads of native heap that glibc never gives
        // back. Two rather than one because libaom splits an AVIF encode across them for about the
        // memory of one thread (a 14 MP page: 672 MB at one, 689 MB at four, 3.4x faster at four), so
        // the gate runs half as many AVIF pages at once instead. A process global: never set per call.
        global::NetVips.NetVips.Concurrency = EncoderThreads;

        global::NetVips.NetVips.BlockUntrusted = true;
        Operation.Block("VipsForeignLoad", true);
        foreach (var loader in AllowedLoaders)
        {
            Operation.Block(loader, false);
        }
    }

    /// <summary>The libvips worker threads of one pipeline, which an AVIF encode uses in full.</summary>
    public const int EncoderThreads = 2;

    /// <summary>Bytes libvips has allocated through its own tracker: pixel buffers, not every malloc it makes.</summary>
    public static long TrackedMemoryBytes => Stats.Mem;

    /// <summary>Reads width, height and format from the header. Throws when the bytes are not an image a permitted loader accepts.</summary>
    public static ImageHeader Identify(byte[] bytes) => Identify(bytes.AsSpan());

    /// <inheritdoc cref="Identify(byte[])"/>
    public static ImageHeader Identify(ReadOnlySpan<byte> bytes)
    {
        if (TryBmp(bytes, out var bmp)) return bmp;
        using var image = VipsImage.NewFromBuffer(bytes, access: Enums.Access.Sequential);
        return Header(image, bytes);
    }

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

    /// <summary>Width, height and bands from the header, or null when no permitted loader reads it.</summary>
    public static (int Width, int Height, int Bands)? TryShape(byte[] bytes)
    {
        try
        {
            using var image = VipsImage.NewFromBuffer(bytes, access: Enums.Access.Sequential);
            return (image.Width, image.Height, image.Bands);
        }
        catch (VipsException)
        {
            return null;
        }
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

    /// <summary>
    /// Whether a page is in colour, or null when it does not load. One band or a B_W header is grey
    /// from the header alone. Three bands are checked on a small thumbnail, because scans are often
    /// stored as RGB with equal channels: colour when more than 2% of it has an LCh chroma over 20,
    /// which yellowed paper stays under.
    /// </summary>
    public static bool? IsColour(byte[] bytes)
    {
        try
        {
            using var header = VipsImage.NewFromBuffer(bytes, access: Enums.Access.Sequential);
            if (header.Bands < 3 || header.Interpretation is Enums.Interpretation.Bw or Enums.Interpretation.Grey16)
            {
                return false;
            }

            if ((long)header.Width * header.Height > Reading.ArchiveHealthAnalyzer.MaxPixels)
            {
                return null;
            }

            using var thumb = VipsImage.ThumbnailBuffer(bytes, ColourThumbWidth);
            return Colourful(thumb);
        }
        catch (VipsException)
        {
            return null;
        }
    }

    private const int ColourThumbWidth = 64;

    private static bool Colourful(VipsImage thumb)
    {
        using var rgb = thumb.HasAlpha() ? thumb.Flatten(background: [255, 255, 255]) : thumb.Copy();
        using var lch = rgb.Colourspace(Enums.Interpretation.Lch);
        using var chroma = lch[1];
        using var colourful = chroma > 20;
        return colourful.Avg() / 255 > 0.02;
    }

    /// <summary>
    /// Sharp YUV only helps chroma edges. On a grey page, most of a manga library and often stored as
    /// RGB, it doubles a WebP encode's time and memory for the same bytes. The check thumbnails the
    /// page already loaded for the encode, so the page is decoded once.
    /// </summary>
    internal static bool SharpYuvHelps(VipsImage image)
    {
        if (image.Bands < 3 || image.Interpretation is Enums.Interpretation.Bw or Enums.Interpretation.Grey16)
        {
            return false;
        }

        using var thumb = image.ThumbnailImage(ColourThumbWidth);
        return Colourful(thumb);
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
        var loader = VipsImage.FindLoadBuffer(bytes);
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
    /// About four times faster than libvips' default of 4 for pages roughly 3% larger at the same
    /// quality, measured on JPEG and PNG chapters. AV1 at 4 put a library pass at hundreds of hours.
    /// </summary>
    public const int AvifEffort = 3;

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
            PageEncoding.Webp => image.WebpsaveBuffer(q: quality, effort: 4, smartSubsample: SharpYuvHelps(image), keep: Enums.ForeignKeep.Icc),
            PageEncoding.Avif => image.HeifsaveBuffer(q: quality, bitdepth: 8, compression: Enums.ForeignHeifCompression.Av1,
                effort: AvifEffort, keep: Enums.ForeignKeep.Icc),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
    }

    /// <summary>
    /// The largest source page the server upscaler takes. Its memory grows with the page, to about
    /// 2 GB at the encode stage for a 40 MP source. Bigger pages are skipped and the reader's browser
    /// fallback covers them.
    /// </summary>
    public const long MaxUpscalePixels = 16_000_000;

    /// <summary>
    /// Decodes a page for an upscale model, or returns a reason it cannot be: the bytes do not decode
    /// cleanly (<see cref="UpscaleReasons.Unreadable"/>), more than 8 bits per channel, or over
    /// <see cref="PageConversion.MaxEdge"/> or <see cref="MaxUpscalePixels"/>.
    /// Orientation is applied, transparency lands on white, an embedded profile, CMYK and greyscale
    /// become sRGB, and the result always has 3 bands. The output is untagged sRGB, so a page in
    /// another profile (Adobe RGB colour pages) has to be converted here or it shows washed out.
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
                (long)loaded.Width * loaded.Height > MaxUpscalePixels)
            {
                return (null, UpscaleReasons.TooLarge);
            }

            using var rotated = loaded.Autorot();
            using var flat = rotated.HasAlpha() ? rotated.Flatten(background: [255, 255, 255]) : rotated.Copy();
            using var converted = Raster.ToSrgb(flat);
            // A grey profile converts to three bands a level apart, which would lose the one-band path.
            using var srgb = flat.Bands == 1 && converted.Bands > 1 ? converted.ExtractBand(1) : converted.Copy();
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

    /// <summary>
    /// <see cref="ResizeRgb(byte[], int, int, double)"/> into <paramref name="into"/>, which must hold the
    /// result. Only the first <c>width * height * 3</c> bytes of <paramref name="rgb"/> are read.
    /// </summary>
    public static (int Width, int Height) ResizeRgb(byte[] rgb, int width, int height, double scale, byte[] into)
    {
        using var image = FromMemory(rgb, width, height, 3);
        using var resized = image.Resize(scale, kernel: Enums.Kernel.Lanczos3);
        var pixels = resized.WriteToMemory(out var size);
        try
        {
            if (size > (ulong)into.Length)
            {
                throw new ArgumentException("The buffer is smaller than the resized image.", nameof(into));
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, into, 0, (int)size);
        }
        finally
        {
            global::NetVips.NetVips.Free(pixels);
        }

        return (resized.Width, resized.Height);
    }

    /// <summary>WebP's hard limit on either edge; an upscaled page past it is saved as JPEG.</summary>
    public const int MaxWebpEdge = 16383;

    /// <summary>
    /// Effort 2 encodes 2.3 to 2.9x faster than 4. Colour pages take q88 there, which beats effort 4's
    /// q85 on PSNR and dark-area error for files 12 to 26% larger. Grey pages stay at q85: q88 is the
    /// usual step, about 1.3 dB for 10% more bytes, on most of a library.
    /// </summary>
    internal const int UpscaledWebpEffort = 2;

    internal const int UpscaledColourQuality = 88;

    internal const int UpscaledGreyQuality = 85;

    /// <summary>
    /// JPEG XL for upscaled colour pages: at the same SSIMULACRA2 as WebP q88 effort 2 it is 14% smaller
    /// and 12% faster on Infinite Mage pages, and it keeps dark gradients smooth where WebP turns them
    /// into 4 to 8 px blocks. Grey pages stay WebP, which was 20% smaller there at the same score.
    /// </summary>
    internal const float UpscaledJpegXlDistance = 1.5f;

    internal const int UpscaledJpegXlEffort = 3;

    /// <summary>
    /// A JPEG XL encode holds 25 to 34 bytes a pixel against WebP's 5 to 7, so bigger outputs (a 16 MP
    /// source upscales to 64 MP) stay WebP or JPEG. 12 MP covers a 1880 x 4000 colour page and a
    /// 1440 x 8000 webtoon slice at about 400 MB.
    /// </summary>
    public const long MaxJpegXlPixels = 12_000_000;

    /// <summary>
    /// Encodes an upscaled page from interleaved 8-bit RGB: JPEG XL for a colour page when
    /// <paramref name="jpegXl"/> is set, libjxl is present and the page is at most
    /// <see cref="MaxJpegXlPixels"/>; otherwise WebP, or JPEG q90 when either edge is over
    /// <see cref="MaxWebpEdge"/>. A failed JPEG XL encode falls back to WebP. A <paramref name="grey"/>
    /// page is saved with one band. The caller decides that from the source page: models tint flat
    /// screentone, so the output can fail the test. Sharp YUV stays off: the chroma comes from the
    /// smoothed, upsampled source, and it doubled the encode time and native peak for no smaller file.
    /// </summary>
    public static UpscaledImage EncodeUpscaled(byte[] rgb, int width, int height, bool grey, bool jpegXl = false)
    {
        if (grey)
        {
            return EncodeUpscaledGrey(UpscalePixels.ToSingleBand(rgb), width, height);
        }

        if (jpegXl && (long)width * height <= MaxJpegXlPixels && JpegXl.IsAvailable)
        {
            try
            {
                return new UpscaledImage(JpegXl.Encode(rgb, width, height, 3, UpscaledJpegXlDistance, UpscaledJpegXlEffort),
                    "jxl", width, height, 3);
            }
            catch (JpegXlException)
            {
            }
        }

        return EncodeBands(rgb, width, height, 3);
    }

    /// <summary>
    /// A cached JPEG XL page as the WebP or JPEG <see cref="EncodeUpscaled"/> would have written, for a
    /// browser that cannot decode JPEG XL. Throws <see cref="JpegXlException"/> when it does not decode.
    /// </summary>
    public static UpscaledImage TranscodeUpscaledJpegXl(byte[] jxl)
    {
        var (pixels, width, height, bands) = JpegXl.Decode(jxl, MaxJpegXlPixels);
        return EncodeBands(pixels, width, height, bands);
    }

    /// <summary>Encodes an upscaled grey page from one 8-bit band per pixel, as <see cref="EncodeUpscaled(byte[], int, int, bool, bool)"/>.</summary>
    public static UpscaledImage EncodeUpscaledGrey(byte[] band, int width, int height) =>
        EncodeBands(band, width, height, 1);

    private static UpscaledImage EncodeBands(byte[] pixels, int width, int height, int bands)
    {
        var grey = bands == 1;
        using var raw = FromMemory(pixels, width, height, bands);
        using var image = raw.Copy(interpretation: grey ? Enums.Interpretation.Bw : Enums.Interpretation.Srgb);
        return Math.Max(width, height) > MaxWebpEdge
            ? new UpscaledImage(image.JpegsaveBuffer(q: 90, subsampleMode: Enums.ForeignSubsample.On,
                keep: Enums.ForeignKeep.None), "jpg", width, height, bands)
            : new UpscaledImage(image.WebpsaveBuffer(q: grey ? UpscaledGreyQuality : UpscaledColourQuality,
                effort: UpscaledWebpEffort, smartSubsample: false, keep: Enums.ForeignKeep.None), "webp", width, height, bands);
    }

    internal static VipsImage Open(byte[] bytes) => VipsImage.NewFromBuffer(bytes, access: Enums.Access.Sequential);

    internal static VipsImage ThumbnailFile(string path, int width, int height) =>
        VipsImage.Thumbnail(path, width, height: height, size: Enums.Size.Force, noRotate: true);

    internal static VipsImage FromMemory(byte[] pixels, int width, int height, int bands) =>
        VipsImage.NewFromMemory(pixels, width, height, bands, Enums.BandFormat.Uchar);

    private static ImageHeader Header(VipsImage image, ReadOnlySpan<byte> bytes)
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
