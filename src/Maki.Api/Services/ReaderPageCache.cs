using Maki.Api.Configuration;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// The disk cache for full-size PDF page renders, shared by the reader and OPDS page endpoints so a
/// PDF volume streamed to a reading app is rendered once, not on every fetch.
/// </summary>
public static class ReaderPageCache
{
    /// <summary>
    /// Full-size PDF page render, disk-cached alongside the thumbnail cache for the same chapter
    /// file so a page opened twice (once by the reader, once to build its thumbnail) is only ever
    /// rendered once. Named <c>{ArchiveVersion}-{index}.full.jpg</c> so it shares the thumbnail
    /// cache's per-directory eviction (missing ChapterFile row, stale archive size) without
    /// colliding with the thumbnail's own <c>{ArchiveVersion}-{index}.jpg</c> name.
    /// </summary>
    public static async Task<string?> GetOrRenderFullPageAsync(
        AppPaths paths, ReaderService.PageSlice slice, int absoluteIndex, string entry, CancellationToken ct)
    {
        var dir = Path.Combine(paths.ReaderCacheDir, slice.ChapterFileId.ToString());
        var cached = Path.Combine(dir, $"{slice.ArchiveVersion}-{absoluteIndex}.full.jpg");
        if (File.Exists(cached))
        {
            return cached;
        }

        await using var source = await CbzReader.OpenPageAsync(slice.ArchivePath, entry, ct);
        if (source is null)
        {
            return null;
        }

        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = File.Create(tmp))
            {
                await source.CopyToAsync(file, ct);
            }

            try
            {
                File.Move(tmp, cached, overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException &&
                                              File.Exists(cached))
            {
                // Another request already finished rendering the same page and has it open for
                // reading (Windows refuses to replace an open file); the bytes are deterministic,
                // so the loser can just use what is there.
                File.Delete(tmp);
            }
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }

        return cached;
    }
}
