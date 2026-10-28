using Maki.Api.Configuration;
using Maki.Core.Images;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// The disk cache of page thumbnails, one 200px JPEG per page under
/// <c>{ReaderCacheDir}/{ChapterFileId}/{ArchiveVersion}-{index}.jpg</c>, shared by the native
/// reader's strip and the OPDS chapter thumbnail so both serve the same file and neither renders
/// what the other already has.
/// </summary>
public sealed class ReaderThumbnailCache(AppPaths paths, ReaderService reader, ILogger<ReaderThumbnailCache> logger)
{
    private const int ThumbnailWidth = 200;

    /// <summary>
    /// A strip thumbnail at quality 80. A narrower page is scaled up so the strip stays even, and a
    /// JPEG shrinks while it decodes, so a 2000x3000 scan never exists as full-size pixels.
    /// </summary>
    internal static byte[] Render(byte[] page) => ImageCodec.JpegThumbnail(page, ThumbnailWidth, 80, enlarge: true);

    /// <summary>
    /// The cached thumbnail of a page, rendered on the first request. <paramref name="page"/> is
    /// zero-based and relative to the chapter's slice. Null when the page cannot be read or decoded.
    /// </summary>
    public async Task<string?> GetOrRenderAsync(ReaderService.PageSlice slice, int page, CancellationToken ct)
    {
        var absoluteIndex = slice.StartPage + page;
        var dir = Path.Combine(paths.ReaderCacheDir, slice.ChapterFileId.ToString());
        var cached = Path.Combine(dir, $"{slice.ArchiveVersion}-{absoluteIndex}.jpg");

        if (File.Exists(cached))
        {
            return cached;
        }

        var entry = slice.Pages[absoluteIndex];
        try
        {
            // A PDF page is resized from its own cached full render rather than re-rendered,
            // so opening a chapter's thumbnail strip does not re-run PDFium for every page it
            // already rendered full-size. That render is already gated by ImageWorkGate on its
            // own (PdfReader.RenderPageAsync), so it stays outside the gate below - nesting
            // would be a reentrant wait on a semaphore that is not reentrant.
            string? fullCached = null;
            if (ComicFile.IsPdf(slice.ArchivePath))
            {
                fullCached = await ReaderPageCache.GetOrRenderFullPageAsync(paths, slice, absoluteIndex, entry, ct);
                if (fullCached is null)
                {
                    return null;
                }
            }

            var missing = false;

            // Gated. A client prefetching a chapter's whole thumbnail strip arrives as dozens
            // of concurrent requests, each decoding a full page to produce a 200px JPEG, and
            // nothing else in this path bounds them. The source page is opened inside the gate
            // too, so a request queued behind it holds no page buffer until its turn comes.
            await ImageWorkGate.RunAsync(async () =>
            {
                var source = fullCached is not null
                    ? File.OpenRead(fullCached)
                    : await reader.OpenPageAsync(slice, entry, ct);

                if (source is null)
                {
                    missing = true;
                    return;
                }

                byte[] bytes;
                await using (source)
                {
                    using var copy = new MemoryStream();
                    await source.CopyToAsync(copy, ct);
                    bytes = copy.ToArray();
                }

                var thumbnail = Render(bytes);

                // Written aside and moved into place: an aborted request would otherwise leave a
                // truncated JPEG at the final path, served as immutable for a year.
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, $"{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(tmp, thumbnail, ct);
                    try
                    {
                        File.Move(tmp, cached, overwrite: true);
                    }
                    catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException &&
                                                      File.Exists(cached))
                    {
                        // Another request finished the same thumbnail and is serving it (Windows
                        // refuses to replace an open file); the bytes match, so use that one.
                        File.Delete(tmp);
                    }
                }
                catch
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    throw;
                }
            }, ct);

            return missing ? null : cached;
        }
        catch (Exception e)
        {
            // A page format libvips is not allowed to open (BMP among them) has no thumbnail.
            logger.LogDebug(e, "Thumbnail failed for chapter {ChapterId} page {Page}", slice.ChapterId, page);
            return null;
        }
    }
}
