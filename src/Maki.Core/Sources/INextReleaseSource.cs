namespace Maki.Core.Sources;

/// <summary>
/// Optional source capability: the chapter listing also states when the next chapter goes up
/// (MANGA Plus does, per title). Read straight after <see cref="ISource.ListChaptersAsync"/>, which
/// is what fills it, so announcing costs no request of its own.
/// </summary>
public interface INextReleaseSource
{
    /// <summary>The next release the last listing of this series announced, or null when it gave none.</summary>
    DateTime? AnnouncedNextRelease(string sourceSeriesId);
}
