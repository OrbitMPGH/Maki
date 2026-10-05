namespace Maki.Core.Import;

/// <summary>
/// Reads the container a file actually is off its first bytes. The extension a release ships with
/// is a claim, not a fact: ".cbz" on a 7z or a RAR is common enough that everything deciding how
/// to open or place an archive has to look at the bytes first.
/// </summary>
public static class ArchiveSignature
{
    public enum Container
    {
        /// <summary>No signature this recognises; the extension is all there is to go on.</summary>
        Unknown,

        /// <summary>A zip, which is what a CBZ is.</summary>
        Zip,

        /// <summary>7z, RAR or a POSIX tar: readable, but not a zip.</summary>
        Other
    }

    /// <summary>The specific container, for naming a mislabelled original after what it really is.</summary>
    public enum Format
    {
        Unknown,
        Zip,
        SevenZip,
        Rar,
        Tar
    }

    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmpty = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] ZipSpanned = [0x50, 0x4B, 0x07, 0x08];
    private static readonly byte[] SevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] Rar = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];
    private static readonly byte[] Ustar = [0x75, 0x73, 0x74, 0x61, 0x72];
    private const int UstarOffset = 257;

    public static Container Sniff(string path) => ToContainer(Detect(path));

    public static Container Sniff(ReadOnlySpan<byte> head) => ToContainer(Detect(head));

    public static Format Detect(string path)
    {
        Span<byte> head = stackalloc byte[UstarOffset + 5];
        int read;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Format.Unknown;
        }

        return Detect(head[..read]);
    }

    public static Format Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(ZipLocalHeader) || head.StartsWith(ZipEmpty) || head.StartsWith(ZipSpanned))
        {
            return Format.Zip;
        }

        if (head.StartsWith(SevenZip))
        {
            return Format.SevenZip;
        }

        if (head.StartsWith(Rar))
        {
            return Format.Rar;
        }

        if (head.Length >= UstarOffset + Ustar.Length && head[UstarOffset..].StartsWith(Ustar))
        {
            return Format.Tar;
        }

        return Format.Unknown;
    }

    /// <summary>The comic extension that names this container, or null for a zip or an unknown one.</summary>
    public static string? ComicExtension(Format format) => format switch
    {
        Format.SevenZip => ".cb7",
        Format.Rar => ".cbr",
        Format.Tar => ".cbt",
        _ => null
    };

    private static Container ToContainer(Format format) => format switch
    {
        Format.Zip => Container.Zip,
        Format.Unknown => Container.Unknown,
        _ => Container.Other
    };
}
