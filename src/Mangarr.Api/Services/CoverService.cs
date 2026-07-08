using Mangarr.Api.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Mangarr.Api.Services;

/// <summary>Downloads series cover art and stores a resized poster under MediaCover/{seriesId}/.</summary>
public class CoverService(IHttpClientFactory httpClientFactory, AppPaths paths, ILogger<CoverService> logger)
{
    private const int PosterWidth = 400;

    public string CoverPathFor(int seriesId) => Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");

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
}
