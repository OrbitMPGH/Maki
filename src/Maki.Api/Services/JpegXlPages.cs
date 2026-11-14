using Maki.Core.Images;
using Maki.Core.Io;
using Maki.Core.Reading;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Services;

/// <summary>
/// Stored pages in JPEG XL, served to clients that may not decode it. The native reader gets the page
/// as it is when the browser says it takes JPEG XL (an <c>Accept</c> that lists <c>image/jxl</c>, or
/// <c>jxl=1</c>, the flag the enhanced page uses); Edge and older browsers, and every OPDS and Komga
/// client, get WebP made on the way out, as a cached JPEG XL upscale is.
/// </summary>
public static class JpegXlPages
{
    public const string MediaType = "image/jxl";

    public static bool IsJpegXl(string entryName) =>
        Path.GetExtension(entryName).Equals(".jxl", StringComparison.OrdinalIgnoreCase);

    public static bool Accepts(HttpRequest request) =>
        request.Query["jxl"] == "1" ||
        request.GetTypedHeaders().Accept.Any(a =>
            a.MediaType.Equals(MediaType, StringComparison.OrdinalIgnoreCase) && a.Quality is not 0);

    /// <summary>What a page goes out as: the content type, and the ETag suffix that keeps the two answers apart.</summary>
    public static (string ContentType, string ETagSuffix) Representation(string entryName, bool acceptsJpegXl) =>
        !IsJpegXl(entryName) ? (CbzReader.ContentType(entryName), "")
        : acceptsJpegXl ? (MediaType, "-jxl")
        : ("image/webp", "-webp");

    /// <summary>
    /// The page as WebP (JPEG past WebP's edge limit), under the image gate at twice the page's megapixels,
    /// which is what a JPEG XL decode and WebP encode peak at. Null when the page does not decode or libjxl
    /// is missing; the caller then sends the stored bytes, which a client that cannot decode JPEG XL fails
    /// on no worse than on a broken page.
    /// </summary>
    public static async Task<(byte[] Bytes, string ContentType)?> TranscodeAsync(byte[] page, CancellationToken ct)
    {
        try
        {
            var (width, height) = JpegXl.ReadSize(page);
            var converted = await ImageWorkGate.RunAsync(
                () => Task.FromResult(ImageCodec.TranscodeJpegXl(page, ArchiveHealthAnalyzer.MaxPixels)),
                2 * ImageWorkGate.Weigh(width, height), ct);
            return (converted.Bytes, converted.Extension == "webp" ? "image/webp" : "image/jpeg");
        }
        catch (Exception ex) when (ex is JpegXlException or NetVips.VipsException)
        {
            return null;
        }
    }

    /// <summary><see cref="TranscodeAsync(byte[], CancellationToken)"/> for a page still in its archive stream, which this disposes.</summary>
    public static async Task<(byte[] Bytes, string ContentType)> TranscodeAsync(Stream page, CancellationToken ct)
    {
        byte[] bytes;
        await using (page)
        {
            using var buffer = PooledStreams.Get("jxl-page");
            await page.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        return await TranscodeAsync(bytes, ct) ?? (bytes, MediaType);
    }
}
