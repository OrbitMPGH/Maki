using SixLabors.ImageSharp;

namespace Maki.Core.Download;

public static class ImageValidator
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] Gif = "GIF8"u8.ToArray();
    private static readonly byte[] Riff = "RIFF"u8.ToArray(); // WebP container

    /// <summary>Cheap validity check: known magic bytes plus a decodable image header.</summary>
    public static async Task<bool> IsValidImageAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 128)
            {
                return false;
            }

            var header = new byte[12];
            await using (var stream = File.OpenRead(filePath))
            {
                if (await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct) < 4)
                {
                    return false;
                }
            }

            if (!HasKnownMagic(header))
            {
                // AVIF/HEIF have an ftyp box at offset 4.
                if (!(header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p'))
                {
                    return false;
                }

                return true; // ImageSharp can't identify AVIF; trust the container magic.
            }

            var imageInfo = await Image.IdentifyAsync(filePath, ct);
            return imageInfo.Width > 0 && imageInfo.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasKnownMagic(byte[] header) =>
        header.AsSpan().StartsWith(Jpeg) ||
        header.AsSpan().StartsWith(Png) ||
        header.AsSpan().StartsWith(Gif) ||
        header.AsSpan().StartsWith(Riff);
}
