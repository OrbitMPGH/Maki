namespace Maki.Metadata.MangaBaka;

/// <summary>
/// A Discover catalogue-browse rail, backed by a single ordering over the local MangaBaka dump.
/// See <see cref="MangaBakaLocalStore.GetBrowseAsync"/>.
/// </summary>
public enum BrowseFeed
{
    /// <summary>Biggest recent climbers in global popularity rank (last month).</summary>
    Trending,

    /// <summary>Highest global popularity rank overall.</summary>
    Popular,

    /// <summary>Most recently published, capped at today (the dump lists future titles).</summary>
    New,

    /// <summary>Highest rated, gated to reasonably-popular titles so 1-vote scores can't win.</summary>
    TopRated,

    /// <summary>Most popular Korean titles (rank within the manhwa type).</summary>
    PopularManhwa,

    /// <summary>Most popular Chinese titles (rank within the manhua type).</summary>
    PopularManhua,
}
