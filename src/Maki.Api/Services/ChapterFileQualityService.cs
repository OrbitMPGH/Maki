using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Quality;
using Maki.Core.Sources;

namespace Maki.Api.Services;

/// <summary>
/// Stamps a <see cref="ChapterFile"/> with its quality tier, release group and page measurement.
/// Write sites that already hold the finished archive call <see cref="Stamp"/>; the ones that must
/// not open it call <see cref="StampTierOnly"/> and leave <c>MeasuredAtUtc</c> null, so
/// <see cref="ChapterFileMeasureService"/> measures them later.
/// </summary>
public class ChapterFileQualityService(
    SourceRegistry sources, TimeProvider time, ILogger<ChapterFileQualityService> logger)
{
    public SourceKind? KindOf(string sourceName) => sources.Find(sourceName)?.Kind;

    /// <summary>The whole site is the group when a scanlator source's chapter doesn't name one itself.</summary>
    public static string? SiteGroup(ISource? source) =>
        source?.Kind == SourceKind.Scanlator ? source.DisplayName : null;

    /// <summary>
    /// Where a file came from. The group is the one the chapter's link to the same source recorded,
    /// falling back to <see cref="SiteGroup"/>; for a file with no registered source (an import, a
    /// torrent, a relink) it is whatever the release name's tags name.
    /// Expects <paramref name="chapter"/>'s <c>SourceLinks</c> and their <c>SourceMapping</c> loaded.
    /// </summary>
    public (SourceKind? Kind, string? Group) ResolveProvenance(ChapterFile file, Chapter? chapter)
    {
        var source = sources.Find(file.SourceName);
        var link = chapter?.SourceLinks.FirstOrDefault(l =>
            l.SourceMapping is { } mapping &&
            string.Equals(mapping.SourceName, file.SourceName, StringComparison.OrdinalIgnoreCase));
        if (link?.Group is { } linkGroup)
        {
            return (source?.Kind, linkGroup);
        }

        if (source is null)
        {
            var parsed = ReleaseNameParser.ParseFileName(file.ReleaseName ?? Path.GetFileName(file.RelativePath));
            return (null, ReleaseTags.Group(parsed.Tags));
        }

        return (source.Kind, SiteGroup(source));
    }

    /// <summary>
    /// Never downgrades: an Unknown tier or a null group leaves whatever the row already had, so a
    /// re-stamp that learns less than an earlier one keeps the earlier answer.
    /// </summary>
    public static void StampTierOnly(ChapterFile file, SourceKind? sourceKind, string? group)
    {
        var fileName = Path.GetFileName(file.RelativePath);
        var isVolume = ReleaseNameParser.ParseFileName(fileName).IsVolume;
        var tier = QualityTierResolver.Resolve(sourceKind, file.ReleaseName, fileName, isVolume);
        if (tier != QualityTier.Unknown)
        {
            file.Tier = tier;
        }

        file.Group = group ?? file.Group;
    }

    /// <summary>
    /// Tier, group and a measurement of the archive at <paramref name="absolutePath"/>. A file that
    /// is not on disk, or that cannot be read right now (locked, share offline), stays unmeasured so
    /// a later pass retries it. One whose bytes fail to measure is still stamped, as zero pages of
    /// unknown format, so the backfill never retries the same bad file forever.
    /// </summary>
    /// <param name="sampleSize">Pages to decode; 0 reads every page.</param>
    public void Stamp(ChapterFile file, string absolutePath, SourceKind? sourceKind, string? group,
        int sampleSize, CancellationToken ct)
    {
        StampTierOnly(file, sourceKind, group);

        if (!File.Exists(absolutePath))
        {
            logger.LogDebug("Not measuring chapter file {Id}: {Path} is not on disk", file.Id, absolutePath);
            return;
        }

        ChapterFileMeasurement measurement;
        try
        {
            measurement = ChapterFileMeasurer.MeasureArchive(absolutePath, sampleSize, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Not measuring chapter file {Id}: {Path} could not be read", file.Id, absolutePath);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not measure chapter file {Id} at {Path}", file.Id, absolutePath);
            measurement = new ChapterFileMeasurement(0, null, null, "unknown");
        }

        file.PageCount = measurement.PageCount;
        file.MedianWidth = measurement.MedianWidth;
        file.MedianHeight = measurement.MedianHeight;
        file.ImageFormat = measurement.ImageFormat;
        file.MeasuredAtUtc = time.GetUtcNow().UtcDateTime;
    }
}
