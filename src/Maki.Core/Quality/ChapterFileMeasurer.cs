using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Maki.Core.Reading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Maki.Core.Quality;

/// <summary>
/// Measures page count, typical dimensions and image format for a chapter file, used to compare
/// two candidate copies of the same chapter. Never throws on a corrupt page or archive: a page
/// ImageSharp cannot decode is skipped and the rest of the chapter is still measured. An
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> opening or reading the
/// file does propagate, since a locked file or an unreachable share says nothing about the bytes.
/// Every stream and archive handle is closed before returning, since a handle left open on
/// Windows blocks a later <c>File.Move</c> over the same path.
/// </summary>
public static class ChapterFileMeasurer
{
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cbz", ".zip" };

    /// <summary>
    /// How much of each page is read. Every format's dimensions sit in its first few KB; the rest
    /// is headroom for EXIF and ICC blocks ahead of a JPEG frame header.
    /// </summary>
    internal const int HeaderBytes = 256 * 1024;

    private static readonly ChapterFileMeasurement Unreadable = new(0, null, null, "unknown");

    /// <summary>Measures pages already read into memory, e.g. by the download processor.</summary>
    public static ChapterFileMeasurement Measure(IReadOnlyList<(string Name, byte[] Bytes)> pages)
    {
        var tally = new Tally();
        foreach (var (name, bytes) in pages)
        {
            tally.Add(name, bytes, bytes.Length);
        }

        return tally.Result(pages.Count);
    }

    private sealed class Tally
    {
        private readonly List<int> _widths = [];
        private readonly List<int> _heights = [];
        private readonly List<string> _formats = [];

        public void Add(string name, byte[] buffer, int length)
        {
            var bytes = buffer.AsSpan(0, length);
            if (IsAvif(bytes))
            {
                // ImageSharp has no AVIF decoder registered, so Identify would just throw on one.
                // The signature alone still says what format the page is in.
                _formats.Add("avif");
                return;
            }

            IImageFormat? format = null;
            try
            {
                format = Image.DetectFormat(bytes);
            }
            catch
            {
                // Sniffing failed; FormatFromExtension below is the fallback.
            }

            try
            {
                using var stream = new MemoryStream(buffer, 0, length, writable: false);
                var info = Image.Identify(stream);
                _widths.Add(info.Width);
                _heights.Add(info.Height);
                _formats.Add(FormatName(format) ?? FormatFromExtension(name));
            }
            catch when (TryPngSize(bytes, out var width, out var height))
            {
                // ImageSharp refuses a PNG whose chunks run past the end of the buffer, which is
                // every PNG larger than HeaderBytes. IHDR is always first, so read it directly.
                _widths.Add(width);
                _heights.Add(height);
                _formats.Add("png");
            }
            catch
            {
                // Undecodable, or a header cut short by HeaderBytes. The signature still names the
                // format when it sniffed; otherwise the page is skipped entirely.
                if (FormatName(format) is { } sniffed) _formats.Add(sniffed);
            }
        }

        public ChapterFileMeasurement Result(int pageCount) =>
            new(pageCount, Median(_widths), Median(_heights), ResolveFormat(_formats));
    }

    /// <summary>
    /// Measures a chapter file on disk. A .cbz/.zip samples <paramref name="sampleSize"/> pages
    /// spread evenly across the archive's own page order (<see cref="CbzReader.PageNames"/>)
    /// rather than reading every page of a large archive; <paramref name="sampleSize"/> &lt;= 0
    /// reads them all. <see cref="ChapterFileMeasurement.PageCount"/> is always the archive's full
    /// count, sampled or not. A .pdf reports its page count only; dimensions are never rendered
    /// here, that is what the reader is for. Any other extension reports an empty, unknown
    /// measurement rather than throwing. Only the first <see cref="HeaderBytes"/> of each page are
    /// read, so measuring every page of a large archive stays cheap.
    /// </summary>
    public static ChapterFileMeasurement MeasureArchive(string path, int sampleSize, CancellationToken ct)
    {
        if (ComicFile.IsPdf(path))
        {
            if (PdfReader.TryPageCount(path, out var count))
            {
                return new ChapterFileMeasurement(count, null, null, "pdf");
            }

            // pdfium reports a locked file the same as a corrupt one. Opening it here surfaces the
            // IO error instead, so only a file that opens fine is recorded as unreadable.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
            return new ChapterFileMeasurement(0, null, null, "pdf");
        }

        if (!ArchiveExtensions.Contains(Path.GetExtension(path)))
        {
            return Unreadable;
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            return Unreadable;
        }

        try
        {
            var pageNames = CbzReader.PageNames(archive);
            var tally = new Tally();
            // One buffer for every page, so a full pass over a large chapter holds 256 KB, not the chapter.
            var buffer = new byte[HeaderBytes];
            foreach (var name in Sample(pageNames, sampleSize))
            {
                ct.ThrowIfCancellationRequested();
                int length;
                try
                {
                    using var entry = CbzReader.OpenEntry(archive, name);
                    if (entry is null) continue;
                    length = entry.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                }
                catch (InvalidDataException)
                {
                    // A damaged entry is a corrupt page, skipped like one that will not decode.
                    continue;
                }

                tally.Add(name, buffer, length);
            }

            return tally.Result(pageNames.Count);
        }
        finally
        {
            archive.Dispose();
        }
    }

    /// <summary>Every page name when there are fewer than <paramref name="sampleSize"/> or it is not positive.</summary>
    private static List<string> Sample(List<string> pageNames, int sampleSize)
    {
        if (sampleSize <= 0 || pageNames.Count <= sampleSize) return pageNames;

        var result = new List<string>(sampleSize);
        for (var i = 0; i < sampleSize; i++)
        {
            result.Add(pageNames[(int)((long)i * pageNames.Count / sampleSize)]);
        }

        return result;
    }

    /// <summary>
    /// Sniffs an AVIF file by its ISO base media "ftyp" box (size, then "ftyp", then a 4-byte
    /// brand) in the first 16 bytes.
    /// </summary>
    private static bool IsAvif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return false;
        if (bytes[4] != (byte)'f' || bytes[5] != (byte)'t' || bytes[6] != (byte)'y' || bytes[7] != (byte)'p')
            return false;
        var brand = Encoding.ASCII.GetString(bytes.Slice(8, 4));
        return brand is "avif" or "avis";
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The 8-byte signature, then IHDR's length and type, then width and height big-endian.</summary>
    private static bool TryPngSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 24 || !bytes.StartsWith(PngSignature) || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return false;
        width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        return width > 0 && height > 0;
    }

    public static string? FormatName(IImageFormat? format)
    {
        if (format is null) return null;
        return format.Name.ToUpperInvariant() switch
        {
            "JPEG" => "jpg",
            "PNG" => "png",
            "WEBP" => "webp",
            "GIF" => "gif",
            _ => format.Name.ToLowerInvariant()
        };
    }

    private static string FormatFromExtension(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "jpg",
        ".png" => "png",
        ".webp" => "webp",
        ".gif" => "gif",
        ".avif" => "avif",
        ".pdf" => "pdf",
        _ => "unknown"
    };

    private static string ResolveFormat(List<string> formats)
    {
        if (formats.Count == 0) return "unknown";
        var distinct = formats.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 1 ? distinct[0] : "mixed";
    }

    public static int? Median(List<int> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
