using Maki.Api.Configuration;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

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
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
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

    public async Task<string?> DownloadCoverAsync(int seriesId, string coverUrl, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient("covers");
            await using var stream = await client.GetStreamAsync(coverUrl, ct);
            using var image = await Image.LoadAsync(stream, ct);

            if (image.Width > PosterWidth)
            {
                image.Mutate(x => x.Resize(PosterWidth, 0));
            }

            var target = CoverPathFor(seriesId);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await image.SaveAsync(target, new JpegEncoder { Quality = 90 }, ct);
            return target;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download cover for series {SeriesId} from {Url}", seriesId, coverUrl);
            return null;
        }
    }

    /// <summary>
    /// Copies the cached poster into the series' own library folder as "cover.jpg", for readers
    /// (Komga, Kavita) that pick up a poster placed directly next to the series' files rather than
    /// through Maki. Gated on <see cref="SettingKeys.LibraryWriteCoverToFolder"/>, default off.
    /// </summary>
    public Task WriteLibraryCoverAsync(Series series, CancellationToken ct = default) =>
        series.RootFolder is null
            ? Task.CompletedTask
            : WriteLibraryCoverAsync(series.Id, Path.Combine(series.RootFolder.Path, series.FolderName), ct);

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
            Directory.CreateDirectory(seriesFolder);
            File.Copy(source, Path.Combine(seriesFolder, "cover.jpg"), overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write library cover for series {SeriesId} to {Folder}", seriesId, seriesFolder);
        }
    }
}
