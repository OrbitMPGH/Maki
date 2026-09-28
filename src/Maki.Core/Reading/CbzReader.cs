using System.IO.Compression;

namespace Maki.Core.Reading;

/// <summary>
/// Reads pages out of a comic file for display: a CBZ's own entries, or a PDF's pages rendered
/// on demand.
/// <para>
/// <see cref="PageNames"/> is the single definition of a CBZ's page order for the whole
/// codebase — <c>VolumeChapterScanner</c> maps chapter boundaries onto the very same list, so
/// if the two ever disagreed the reader would open a chapter at the wrong page. Adopted and
/// imported archives are never renamed internally, so page names are arbitrary scanlation
/// strings ("... - c049 (v05) - p113 [web] ...png") and nothing may assume Maki's own
/// <c>001.jpg</c> convention.
/// </para>
/// <para>
/// A PDF has no page filenames, so its names are synthesized by <see cref="PdfReader.PageName"/>
/// from the page count and mean nothing outside this pair of methods. They still come from
/// <see cref="PageNames(string)"/>, so the single-definition rule above holds for both formats.
/// </para>
/// </summary>
public static class CbzReader
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp"
    };

    public static bool IsImage(string entryName) =>
        ImageExtensions.Contains(Path.GetExtension(entryName));

    /// <summary>Image entries of an open archive, in reading order.</summary>
    public static List<string> PageNames(ZipArchive archive) =>
        archive.Entries
            .Where(e => IsImage(e.Name))
            .Select(e => e.FullName)
            .OrderBy(n => n, NaturalFileNameComparer.Instance)
            .ToList();

    /// <summary>
    /// Image entries of a CBZ, in reading order. Never throws — an unreadable archive
    /// yields an empty list, matching <c>VolumeChapterScanner</c>.
    /// </summary>
    public static List<string> PageNames(string cbzPath)
    {
        if (ComicFile.IsPdf(cbzPath))
        {
            var count = PdfReader.PageCount(cbzPath);
            return Enumerable.Range(0, count).Select(i => PdfReader.PageName(i, count)).ToList();
        }

        try
        {
            using var archive = ZipFile.OpenRead(cbzPath);
            return PageNames(archive);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Opens one page for streaming. The returned stream owns the archive and closes it on
    /// dispose, so the caller may hand it straight to a <c>FileStreamResult</c>.
    /// Returns null when the entry is missing.
    /// <para>
    /// A PDF page is rendered rather than read out, and every way that can fail - a name outside
    /// the document, an unreadable file, PDFium or ImageSharp giving up - is null here too, so the
    /// caller maps it to the same 404 as a missing zip entry.
    /// </para>
    /// </summary>
    public static Stream? OpenPage(string cbzPath, string entryName)
    {
        if (ComicFile.IsPdf(cbzPath))
        {
            if (!PdfReader.TryParsePageIndex(entryName, out var index)) return null;
            try
            {
                return PdfReader.RenderPage(cbzPath, index);
            }
            catch
            {
                return null;
            }
        }

        // ZipArchive is not thread-safe, so every call gets its own instance. The reader's hot path
        // goes through ReaderArchiveCache instead, which keeps one per recently read archive.
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(cbzPath);
        }
        catch (InvalidDataException)
        {
            // A corrupt central directory throws this, same "not readable" outcome as below.
            return null;
        }

        try
        {
            var stream = OpenEntry(archive, entryName);
            if (stream is null)
            {
                archive.Dispose();
                return null;
            }

            return new OwningStream(stream, archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One entry of an archive the caller already holds open, or null when it is missing or its
    /// local header is corrupt. The stream reads through <paramref name="archive"/>, so the caller
    /// must not touch the archive from another thread until it is disposed.
    /// </summary>
    public static Stream? OpenEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) return null;
        try
        {
            return entry.Open();
        }
        catch (InvalidDataException)
        {
            // A truncated or corrupt entry throws this out of Open(), treated as a missing entry.
            return null;
        }
    }

    /// <summary>
    /// <see cref="OpenEntry"/> read fully into memory, so the result outlives any lock the caller
    /// holds on <paramref name="archive"/>. A corrupt entry is null here too, including one that
    /// only fails part way through decompression.
    /// </summary>
    public static MemoryStream? ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) return null;
        if (entry.Length > MaxEntryBytes) return null;
        try
        {
            using var source = entry.Open();
            var copy = new MemoryStream(entry.Length is > 0 and < MaxPresizeBytes ? (int)entry.Length : 0);
            // The declared length is untrusted, so the copy is bounded independently of it.
            if (!CopyBounded(source, copy, MaxEntryBytes)) return null;
            copy.Position = 0;
            return copy;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The declared size is untrusted input, so it only pre-sizes the buffer up to this.</summary>
    private const int MaxPresizeBytes = 64 * 1024 * 1024;

    /// <summary>A single page has no business exceeding this; a corrupt or hostile entry that claims more is refused.</summary>
    private const long MaxEntryBytes = 64 * 1024 * 1024;

    /// <summary>Copies at most <paramref name="maxBytes"/> from <paramref name="source"/>, returning false if more remained.</summary>
    private static bool CopyBounded(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes) return false;
            destination.Write(buffer, 0, read);
        }

        return true;
    }

    /// <summary>
    /// <see cref="OpenPage"/> for callers that can await. Only the PDF path is genuinely async:
    /// it renders through <c>ImageWorkGate</c> instead of encoding ungated, and honours
    /// <paramref name="ct"/>. A zip entry opens synchronously and streams lazily either way.
    /// </summary>
    public static async Task<Stream?> OpenPageAsync(string cbzPath, string entryName, CancellationToken ct = default)
    {
        if (!ComicFile.IsPdf(cbzPath)) return OpenPage(cbzPath, entryName);
        if (!PdfReader.TryParsePageIndex(entryName, out var index)) return null;
        try
        {
            return await PdfReader.RenderPageAsync(cbzPath, index, PdfReader.MaxEdge, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static string ContentType(string entryName) => Path.GetExtension(entryName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    /// <summary>A read-only stream that disposes an extra resource alongside itself.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owned) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            inner.ReadAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            inner.ReadAsync(buffer, offset, count, ct);

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owned.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            owned.Dispose();
            await base.DisposeAsync();
        }
    }
}
