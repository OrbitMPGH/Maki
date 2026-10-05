using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Core.Quality;

/// <summary>
/// Assigns a <see cref="QualityTier"/> to a chapter file. A scraped file already carries its
/// source's <c>SourceKind</c>, which maps straight across. Anything else has no source kind at
/// all (an import, a torrent, or a sentinel such as "Manual", "rescan" or "relink"), so its tier
/// comes from reading the release name's own tags instead.
/// </summary>
public static class QualityTierResolver
{
    public static QualityTier Resolve(SourceKind? sourceKind, string? releaseName, string fileName, bool isVolume)
    {
        if (sourceKind is not null)
        {
            return sourceKind.Value switch
            {
                SourceKind.Official => QualityTier.Official,
                SourceKind.Scanlator => QualityTier.Scanlator,
                SourceKind.Aggregator => QualityTier.Aggregator,
                _ => QualityTier.Unknown
            };
        }

        var parsed = ReleaseNameParser.ParseFileName(releaseName ?? fileName);
        if (isVolume && ReleaseTags.IsDigital(parsed.Tags)) return QualityTier.Volume;
        if (ReleaseTags.Group(parsed.Tags) is not null) return QualityTier.Scanlator;
        return QualityTier.Unknown;
    }
}
