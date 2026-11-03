using Maki.Api.Configuration;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Notifications;
using System.Security.Cryptography;

namespace Maki.Api.Services;

/// <summary>Downloads series cover art and stores a resized poster under MediaCover/{seriesId}/.</summary>
public class CoverService(
    IHttpClientFactory httpClientFactory, AppPaths paths, IAppSettings settings, ILogger<CoverService> logger)
    : INotificationCoverStore
{
    private const int PosterWidth = 400;

    public string CoverPathFor(int seriesId) => Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");

    /// <summary>
    /// Removes a series' whole poster folder. Must run on series delete: SQLite reuses a rowid
    /// once the highest-id row is removed, so a later series can be assigned the same id — and
    /// <see cref="MediaCoverController"/> resolves a cover purely by id, with no check that the
    /// file on disk belongs to the series that still exists. Leaving the folder behind means the
    /// new series serves the deleted one's cover until its own download happens to overwrite it.
    /// </summary>
    public void DeleteCover(int seriesId)
    {
        var dir = Path.GetDirectoryName(CoverPathFor(seriesId))!;
        CoverVersionCache.Remove(CoverPathFor(seriesId));
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the cover folder {Folder} for series {SeriesId}", dir, seriesId);
        }
    }

    /// <summary>
    /// Explicit so the existing <see cref="CoverPathFor"/> keeps its "where it would live" meaning —
    /// notification providers need "is there actually one to upload".
    /// </summary>
    string? INotificationCoverStore.PosterPathFor(int seriesId)
    {
        var path = CoverPathFor(seriesId);
        return File.Exists(path) ? path : null;
    }

    private static bool SameBytes(string a, string b)
    {
        var first = new FileInfo(a);
        var second = new FileInfo(b);
        return second.Exists && first.Length == second.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    public async Task<string?> DownloadCoverAsync(int seriesId, string coverUrl, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient("covers");
            await using var stream = await client.GetStreamAsync(coverUrl, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            return await ImageWorkGate.RunAsync(
                () => Task.FromResult(SavePoster(seriesId, bytes)), ImageWorkGate.Weigh(bytes), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download cover for series {SeriesId} from {Url}", seriesId, coverUrl);
            return null;
        }
    }

    /// <summary>
    /// Stores an image a user uploaded as the series' poster, through the same resize and encode as a
    /// downloaded one. The caller has already checked the bytes are a raster image of sane size.
    /// Orientation is applied and every metadata block dropped (<see cref="ImageCodec.JpegThumbnail"/>
    /// does both for every poster), so a phone photo's EXIF location never reaches the poster other
    /// users can fetch.
    /// </summary>
    public Task<string> StoreUploadedCoverAsync(int seriesId, Stream content, CancellationToken ct = default) =>
        ImageWorkGate.RunAsync(async () =>
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            return SavePoster(seriesId, buffer.ToArray());
        }, ct);

    /// <summary>The first frame of an animation, shrunk to <see cref="PosterWidth"/> when wider and never enlarged.</summary>
    private string SavePoster(int seriesId, byte[] source)
    {
        var poster = ImageCodec.JpegThumbnail(source, PosterWidth, 90, enlarge: false);
        var target = CoverPathFor(seriesId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, poster);
            if (SameBytes(temp, target))
            {
                // Keeps the write time, which is what the cover URL's cache-buster follows.
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, target, overwrite: true);
                CoverVersionCache.Set(target, File.GetLastWriteTimeUtc(target).Ticks);
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        return target;
    }

    /// <summary>
    /// Copies the cached poster into the series' own library folder as "cover.jpg", for readers
    /// (Komga, Kavita) that pick up a poster placed directly next to the series' files rather than
    /// through Maki. Gated on <see cref="SettingKeys.LibraryWriteCoverToFolder"/>, default off. A cover.jpg
    /// the user placed there is left alone; only one Maki wrote is refreshed.
    /// </summary>
    public Task WriteLibraryCoverAsync(Series series, CancellationToken ct = default) =>
        series.RootFolder is null
            ? Task.CompletedTask
            : WriteLibraryCoverAsync(series.Id, Path.Combine(series.RootFolder.Path, series.FolderName), ct);

    /// <summary>
    /// Hash of the last cover.jpg Maki wrote into the series folder. A cover.jpg that matches neither it
    /// nor the current poster was put there by the user and is never overwritten.
    /// </summary>
    private string LibraryCoverRecordFor(int seriesId) =>
        Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "library-cover.sha256");

    private static bool WrittenByMaki(string cover, string record) =>
        File.Exists(record) && File.ReadAllText(record).Trim() == Sha256Of(cover);

    /// <summary>
    /// True for a cover.jpg Maki wrote: it matches the hash record or is the current poster. Anything
    /// else in a series folder is the user's.
    /// </summary>
    public bool IsMakiLibraryCover(int seriesId, string cover)
    {
        try
        {
            return File.Exists(cover)
                   && (WrittenByMaki(cover, LibraryCoverRecordFor(seriesId)) || SameBytes(cover, CoverPathFor(seriesId)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The SHA-256 of the cover.jpg Maki last wrote into the series folder, or null.</summary>
    public string? LibraryCoverHash(int seriesId)
    {
        var record = LibraryCoverRecordFor(seriesId);
        try
        {
            return File.Exists(record) ? File.ReadAllText(record).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <inheritdoc cref="WriteLibraryCoverAsync(Series, CancellationToken)"/>
    public async Task WriteLibraryCoverAsync(int seriesId, string seriesFolder, CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingKeys.LibraryWriteCoverToFolder, ct) != "true")
        {
            return;
        }

        var source = CoverPathFor(seriesId);
        if (!File.Exists(source))
        {
            return;
        }

        try
        {
            var target = Path.Combine(seriesFolder, "cover.jpg");
            var record = LibraryCoverRecordFor(seriesId);
            if (File.Exists(target) && !SameBytes(source, target) && !WrittenByMaki(target, record))
            {
                return;
            }

            Directory.CreateDirectory(seriesFolder);
            File.Copy(source, target, overwrite: true);
            File.WriteAllText(record, Sha256Of(target));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write library cover for series {SeriesId} to {Folder}", seriesId, seriesFolder);
        }
    }
}
