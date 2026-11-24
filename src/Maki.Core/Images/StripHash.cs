using NetVips;

namespace Maki.Core.Images;

/// <summary>
/// A chapter's pages as one continuous strip: every page scaled to <see cref="Width"/> pixels wide and
/// stacked, then a square window every <see cref="Step"/> rows gets a difference hash
/// (<see cref="PerceptualHash"/>). Per-page hashes miss a source that cuts each page into slices
/// (TopManhua), pads it with a crop (MangaKakalot) or cuts a webtoon strip at other points; on the
/// strip those are the same picture again. The step is small so two strips of one chapter always
/// have windows within a row of each other, however their pages are cut.
/// </summary>
public static class StripHash
{
    public const int Width = 32;

    public const int Step = 2;

    /// <summary>An image this many times as wide as tall, and up to <see cref="SpreadMax"/>, is a two-page spread.</summary>
    private const double SpreadMin = 1.2;

    private const double SpreadMax = 1.8;

    /// <summary>A strip longer than this (a very long webtoon chapter) is not stored; its pages still are.</summary>
    public const int MaxWindows = 20_000;

    /// <summary>The strip's window hashes, or null when a page does not decode or the strip is too long.</summary>
    public static ulong[]? OfFiles(IReadOnlyList<string> files)
    {
        var pages = new List<byte[]>(files.Count);
        var rows = 0;
        foreach (var file in files)
        {
            var header = ImageCodec.IdentifyFile(file);
            if (header.Width <= 0 || header.Height <= 0)
            {
                return null;
            }

            // A two-page spread is the two pages another source lists one by one: right page first, as
            // manga reads. Wider than that is a strip of one page (TopManhua's bottom slices), left whole.
            var spread = header.Width > header.Height * SpreadMin && header.Width < header.Height * SpreadMax;
            var across = spread ? 2 * Width : Width;
            var height = Math.Max(1, (int)Math.Round((double)header.Height * across / header.Width));
            using var thumb = ImageCodec.ThumbnailFile(file, across, height);
            using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
            using var grey = srgb.Colourspace(Enums.Interpretation.Bw);
            using var flat = grey.HasAlpha() ? grey.Flatten(background: [255]) : grey.Copy();
            if (flat.Width != across || flat.Bands != 1)
            {
                return null;
            }

            var pixels = flat.WriteToMemory<byte>();
            if (spread)
            {
                // Read once and halved here: libvips reads the page sequentially, two crops would not.
                var right = new byte[Width * flat.Height];
                var left = new byte[Width * flat.Height];
                for (var y = 0; y < flat.Height; y++)
                {
                    pixels.AsSpan(y * across, Width).CopyTo(left.AsSpan(y * Width));
                    pixels.AsSpan(y * across + Width, Width).CopyTo(right.AsSpan(y * Width));
                }

                pages.Add(right);
                pages.Add(left);
            }
            else
            {
                pages.Add(pixels);
            }

            rows += (spread ? 2 : 1) * flat.Height;
            if ((rows - Width) / Step > MaxWindows)
            {
                return null;
            }
        }

        var strip = new byte[rows * Width];
        var at = 0;
        foreach (var page in pages)
        {
            page.CopyTo(strip, at);
            at += page.Length;
        }

        return Windows(strip, rows);
    }

    /// <summary>The window hashes of a strip of <paramref name="rows"/> rows, <see cref="Width"/> bytes each.</summary>
    public static ulong[] Windows(byte[] strip, int rows)
    {
        if (rows < Width)
        {
            return [];
        }

        var hashes = new ulong[(rows - Width) / Step + 1];
        for (var i = 0; i < hashes.Length; i++)
        {
            hashes[i] = PerceptualHash.Of(strip.AsSpan(i * Step * Width, Width * Width), Width, Width);
        }

        return hashes;
    }
}
