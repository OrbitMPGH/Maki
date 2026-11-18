namespace Maki.Core.Sources;

/// <summary>
/// Optional source capability: counts a chapter's pages without what <see cref="ISource.GetPagesAsync"/>
/// does on top. TopManhua's page list loads every image in a browser, but the chapter page already
/// names them all. Without it, a page count is the length of <see cref="ISource.GetPagesAsync"/>'s list,
/// and a source declaring <see cref="SourceCapabilities.NeedsBrowser"/> is not counted at all.
/// </summary>
public interface IPageCountSource
{
    Task<int> CountPagesAsync(SourceChapter chapter, CancellationToken ct = default);
}
