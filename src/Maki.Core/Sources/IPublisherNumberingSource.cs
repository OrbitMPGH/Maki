namespace Maki.Core.Sources;

/// <summary>
/// Optional source capability: the chapter listing also carries the official publisher's chapter
/// numbers, even where the chapters themselves are hosted elsewhere (MangaDex lists the Manga UP!
/// and Comikey releases as external chapters). Read straight after
/// <see cref="ISource.ListChaptersAsync"/>, which is what fills it, so it costs no request of its own.
/// A publisher that numbers parts 10.1 to 10.4 is the strongest sign that those are parts of one chapter.
/// </summary>
public interface IPublisherNumberingSource
{
    /// <summary>The publisher's numbers the last listing of this series showed, or null when it showed none.</summary>
    IReadOnlyList<decimal>? PublisherNumbers(string sourceSeriesId);
}
