using NetVips;
using VipsImage = NetVips.Image;

namespace Maki.Core.Images;

public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

/// <summary>
/// Decoded 8-bit sRGB pixels in a plain managed array, for work that moves pixels around rather
/// than filtering them (the page descramblers) and for tests that build or inspect images.
/// <see cref="Bands"/> is 3 (RGB) or 4 (RGBA), interleaved, rows top to bottom with no padding.
/// </summary>
public sealed class Raster
{
    public int Width { get; }
    public int Height { get; }
    public int Bands { get; }
    public byte[] Pixels { get; }

    public int Stride => Width * Bands;

    public Raster(int width, int height, int bands = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (bands is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(bands), bands, "3 or 4 bands");
        Width = width;
        Height = height;
        Bands = bands;
        Pixels = new byte[width * height * bands];
        if (bands == 4)
        {
            for (var i = 3; i < Pixels.Length; i += 4) Pixels[i] = 255;
        }
    }

    public Raster(int width, int height, Rgba fill, int bands = 4) : this(width, height, bands)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            this[x, y] = fill;
    }

    private Raster(int width, int height, int bands, byte[] pixels)
    {
        Width = width;
        Height = height;
        Bands = bands;
        Pixels = pixels;
    }

    public Rgba this[int x, int y]
    {
        get
        {
            var o = Offset(x, y);
            return new Rgba(Pixels[o], Pixels[o + 1], Pixels[o + 2], Bands == 4 ? Pixels[o + 3] : (byte)255);
        }
        set
        {
            var o = Offset(x, y);
            Pixels[o] = value.R;
            Pixels[o + 1] = value.G;
            Pixels[o + 2] = value.B;
            if (Bands == 4) Pixels[o + 3] = value.A;
        }
    }

    public Span<byte> Row(int y) => Pixels.AsSpan(y * Stride, Stride);

    public Raster Clone() => new(Width, Height, Bands, (byte[])Pixels.Clone());

    /// <summary>Copies a <paramref name="width"/> x <paramref name="height"/> block of <paramref name="source"/> into this raster. Both must have the same band count.</summary>
    public void CopyFrom(Raster source, int sourceX, int sourceY, int width, int height, int x, int y)
    {
        if (source.Bands != Bands) throw new ArgumentException("Band counts differ", nameof(source));
        var bytes = width * Bands;
        for (var row = 0; row < height; row++)
        {
            source.Row(sourceY + row).Slice(sourceX * Bands, bytes).CopyTo(Row(y + row).Slice(x * Bands, bytes));
        }
    }

    /// <summary>
    /// Decodes the first frame as 8-bit sRGB. Orientation is left as stored, like the pixels a site
    /// scrambled. With 3 bands any alpha is dropped; with 4 an opaque one is added when missing.
    /// </summary>
    public static Raster Decode(byte[] bytes, int bands = 4)
    {
        if (bands is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(bands), bands, "3 or 4 bands");
        using var loaded = ImageCodec.Open(bytes);
        using var srgb = ToSrgb(loaded);
        using var shaped = bands == 3
            ? (srgb.HasAlpha() ? srgb.ExtractBand(0, n: 3) : srgb.Copy())
            : (srgb.HasAlpha() ? srgb.Copy() : srgb.Bandjoin(255));
        return new Raster(shaped.Width, shaped.Height, bands, shaped.WriteToMemory<byte>());
    }

    /// <summary>JPEG ignores alpha, the way Maki's pages always have: the colour values are kept as they are.</summary>
    public byte[] EncodeJpeg(int quality)
    {
        using var image = ToVips(dropAlpha: true);
        return image.JpegsaveBuffer(q: quality, subsampleMode: Enums.ForeignSubsample.On, keep: Enums.ForeignKeep.None);
    }

    public byte[] EncodeWebp(int quality) =>
        Save(image => image.WebpsaveBuffer(q: quality, keep: Enums.ForeignKeep.None));

    public byte[] EncodeWebpLossless() =>
        Save(image => image.WebpsaveBuffer(lossless: true, exact: true, keep: Enums.ForeignKeep.None));

    /// <param name="compression">zlib level, 0 (none) to 9.</param>
    public byte[] EncodePng(int compression = 6) =>
        Save(image => image.PngsaveBuffer(compression: compression, keep: Enums.ForeignKeep.None));

    public byte[] EncodeGif() => Save(image => image.GifsaveBuffer(keep: Enums.ForeignKeep.None));

    private byte[] Save(Func<VipsImage, byte[]> save)
    {
        using var image = ToVips(dropAlpha: false);
        return save(image);
    }

    private VipsImage ToVips(bool dropAlpha)
    {
        using var raw = ImageCodec.FromMemory(Pixels, Width, Height, Bands);
        if (dropAlpha && Bands == 4)
        {
            using var rgb = raw.ExtractBand(0, n: 3);
            return rgb.Copy(interpretation: Enums.Interpretation.Srgb);
        }

        return raw.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    /// <summary>
    /// Converts whatever was stored (greyscale, CMYK, 16-bit, an embedded profile) to plain 8-bit
    /// sRGB, so the pixel values are the ones a browser would show.
    /// </summary>
    private static VipsImage ToSrgb(VipsImage image)
    {
        if (image.Contains("icc-profile-data"))
        {
            try
            {
                return image.IccTransform("srgb", embedded: true, depth: 8);
            }
            catch (VipsException)
            {
                // A broken profile is ignored, the way a browser does.
            }
        }

        if (image.Interpretation == Enums.Interpretation.Srgb && image.Format == Enums.BandFormat.Uchar)
        {
            return image.Copy();
        }

        using var converted = image.Colourspace(Enums.Interpretation.Srgb);
        return converted.Format == Enums.BandFormat.Uchar ? converted.Copy() : converted.Cast(Enums.BandFormat.Uchar);
    }

    private int Offset(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) outside {Width}x{Height}");
        return (y * Width + x) * Bands;
    }
}
