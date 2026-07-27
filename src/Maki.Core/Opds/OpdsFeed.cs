namespace Maki.Core.Opds;

/// <summary>
/// Which of the two OPDS 1.2 feed profiles a document is. Clients branch on the <c>kind=</c>
/// parameter of the content type: a navigation feed is a menu, an acquisition feed is a shelf of
/// things you can actually open. Getting it wrong makes readers render chapters as folders.
/// </summary>
public enum OpdsFeedKind
{
    Navigation,
    Acquisition
}

/// <param name="Rel">Atom link relation — "self", "start", "subsection", "search",
/// <c>http://opds-spec.org/acquisition</c>, <c>http://opds-spec.org/image</c>, and so on.</param>
public record OpdsLink(string Rel, string Href, string Type, string? Title = null, long? Length = null);

/// <summary>
/// An OPDS-PSE page-streaming link: the reader fetches one page at a time instead of pulling the
/// whole CBZ down first.
/// </summary>
/// <param name="HrefTemplate">Href containing the literal <c>{pageNumber}</c> placeholder the
/// client substitutes. Zero-based, per the PSE spec — the first page is 0.</param>
/// <param name="Count">Total pages in the chapter. Required by the spec; a reader with no count
/// cannot build its page bar.</param>
/// <param name="LastRead">Pages already read, i.e. a <em>one</em>-based position, which is what
/// clients render as "read N of M". Null when the chapter has never been opened.</param>
public record OpdsPseStream(string HrefTemplate, int Count, int? LastRead, DateTime? LastReadDate);

/// <param name="Id">Stable, opaque, globally unique. Readers key their local state off it, so it
/// must not change between feed renders of the same thing.</param>
/// <param name="Content">Plain-text summary. Deliberately not HTML — feeding a series overview
/// straight through as <c>type="html"</c> would let scraped markup reach the reader app.</param>
public record OpdsEntry(
    string Id,
    string Title,
    DateTime Updated,
    string? Content = null,
    string? Author = null,
    IReadOnlyList<OpdsLink>? Links = null,
    OpdsPseStream? Stream = null,
    IReadOnlyList<string>? Categories = null);

/// <param name="TotalResults">OpenSearch paging hints. Set on paged feeds so a reader can show
/// "50 of 1200" rather than silently stopping at whatever the first page held.</param>
public record OpdsFeed(
    string Id,
    string Title,
    DateTime Updated,
    OpdsFeedKind Kind,
    IReadOnlyList<OpdsLink> Links,
    IReadOnlyList<OpdsEntry> Entries,
    int? TotalResults = null,
    int? ItemsPerPage = null,
    int? StartIndex = null);
