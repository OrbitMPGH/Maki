using Docnet.Core;
using Docnet.Core.Models;
using Maki.Core.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Core.Reading;

/// <summary>
/// Renders the pages of a PDF read in place. The file is never converted: the reader asks for a
/// page by the synthetic name <see cref="PageName"/> produces and gets a JPEG rasterized on the
/// spot.
/// <para>
/// PDFium is not thread-safe, so every call into Docnet - open, count, render, dispose - happens
/// under one process-wide lock. The lock is held only until the BGRA buffer is out of PDFium's
/// hands; the ImageSharp work that follows runs outside it, so callers do not queue on PDFium
/// while someone else encodes a JPEG.
/// </para>
/// <para>
/// <see cref="ImageWorkGate"/> bounds how many callers hold decoded pixels at once. It only has an
/// async entry, so the async path takes a permit around rasterizing and encoding together - the
/// BGRA buffer is never allocated while a caller is still queueing for a permit - and the sync path
/// does both steps ungated rather than blocking on a semaphore from inside a request thread. The
/// PDFium lock stays scoped to the rasterize call in both paths.
/// </para>
/// </summary>
public static class PdfReader
{
    /// <summary>
    /// Longest edge of a rendered reader page. Pages are rasterized to fit a square of this size
    /// with their aspect ratio kept, so a portrait page comes out 2000 px tall.
    /// </summary>
    public const int MaxEdge = 2000;

    /// <summary>
    /// Edge used by the health analyzer's verify and by the health preview that has to match it.
    /// A page's RawHash is the SHA-256 of a render at this size, so both sides must ask for the
    /// same number or every page reads as changed.
    /// </summary>
    public const int FingerprintEdge = 800;

    private static readonly object Pdfium = new();

    /// <summary>Pages in the document, or 0 when it cannot be opened.</summary>
    public static int PageCount(string path) => TryPageCount(path, out var count) ? count : 0;

    /// <summary>
    /// Page count, distinguishing a document that would not open (false) from one that opened and
    /// holds no pages (true, 0). The health analyzer needs the difference; readers do not.
    /// </summary>
    internal static bool TryPageCount(string path, out int count)
    {
        try
        {
            lock (Pdfium)
            {
                using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(1));
                count = doc.GetPageCount();
                return true;
            }
        }
        catch
        {
            count = 0;
            return false;
        }
    }

    /// <summary>
    /// Renders one zero-based page to a JPEG stream positioned at 0, fitted to a
    /// <paramref name="maxEdge"/> square with its aspect ratio kept. Throws when the index is out
    /// of range or the document cannot be read.
    /// </summary>
    public static MemoryStream RenderPage(string path, int index, int maxEdge = MaxEdge)
    {
        var raw = Rasterize(path, index, maxEdge);
        return Encode(raw);
    }

    /// <inheritdoc cref="RenderPage"/>
    public static async Task<MemoryStream> RenderPageAsync(string path, int index, int maxEdge = MaxEdge,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await ImageWorkGate.RunAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var raw = Rasterize(path, index, maxEdge);
            return Task.FromResult(Encode(raw));
        }, ct);
    }

    private readonly record struct RawPage(byte[] Bgra, int Width, int Height);

    private static RawPage Rasterize(string path, int index, int maxEdge)
    {
        var edge = Math.Max(1, maxEdge);
        lock (Pdfium)
        {
            using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(edge, edge));
            if (index < 0 || index >= doc.GetPageCount())
                throw new ArgumentOutOfRangeException(nameof(index));
            using var page = doc.GetPageReader(index);
            return new RawPage(page.GetImage(), page.GetPageWidth(), page.GetPageHeight());
        }
    }

    private static MemoryStream Encode(RawPage raw)
    {
        using var image = Image.LoadPixelData<Bgra32>(raw.Bgra, raw.Width, raw.Height);
        // PDFium leaves anything the page did not paint transparent, and JPEG has no alpha to
        // carry it, so an unflattened page encodes as black.
        image.Mutate(x => x.BackgroundColor(Color.White));
        var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder { Quality = 90 });
        stream.Position = 0;
        return stream;
    }

    /// <summary>Synthetic page name for a zero-based index, padded to the digits of <paramref name="count"/>.</summary>
    public static string PageName(int index, int count)
    {
        var digits = Math.Max(4, Math.Max(count, 1).ToString().Length);
        return (index + 1).ToString(new string('0', digits)) + ".jpg";
    }

    /// <summary>Reverses <see cref="PageName"/>.</summary>
    public static bool TryParsePageIndex(string pageName, out int index)
    {
        index = -1;
        if (!Path.GetExtension(pageName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(pageName);
        if (stem.Length == 0 || !stem.All(char.IsAsciiDigit)) return false;
        if (!int.TryParse(stem, out var number) || number < 1) return false;
        index = number - 1;
        return true;
    }
}
