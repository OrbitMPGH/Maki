namespace Maki.Core.Quality;

/// <summary>
/// Provenance tier of a chapter file. Plays the role Sonarr's quality plays: known before
/// download for every source, ordered by a profile, compared before anything else.
/// Numeric order is the default ranking only; profiles may reorder.
/// </summary>
public enum QualityTier
{
    /// <summary>Imported file not yet measured, or provenance lost.</summary>
    Unknown = 0,

    /// <summary>Scraped from a source whose <c>SourceKind</c> is Aggregator.</summary>
    Aggregator = 1,

    /// <summary>Scraped from a Scanlator source, or a torrent whose tags name a group.</summary>
    Scanlator = 2,

    /// <summary>Scraped from an Official source.</summary>
    Official = 3,

    /// <summary>Torrent or import whose release name parses as a volume with a Digital tag.</summary>
    Volume = 4
}
