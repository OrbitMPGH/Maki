using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Maki.Core.Opds;

/// <summary>
/// Renders <see cref="OpdsFeed"/> as OPDS 1.2 (Atom XML).
/// <para>
/// OPDS 1.2 rather than 2.0 on purpose: 2.0 is JSON and barely supported, while every reader this
/// feature exists for — Panels, Chunky, KOReader, the Mihon/Tachiyomi OPDS extensions — speaks 1.2,
/// and all of them get page streaming through the OPDS-PSE extension, which has no 2.0 equivalent.
/// </para>
/// <para>
/// Hrefs are emitted <b>root-relative</b> ("/api/v1/opds/..."), never absolute. An absolute URL
/// would have to be built from <c>Request.Scheme</c>/<c>Host</c>, which is wrong behind any
/// TLS-terminating reverse proxy that doesn't rewrite them — the feed would hand out http:// links
/// to a remote reader. Every client in the list above resolves relative hrefs against the feed URL.
/// </para>
/// </summary>
public static class OpdsXml
{
    public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    public static readonly XNamespace Opds = "http://opds-spec.org/2010/catalog";
    public static readonly XNamespace Pse = "http://vaemendis.net/opds-pse/ns";
    public static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
    public static readonly XNamespace Dc = "http://purl.org/dc/terms/";

    public const string NavigationType = "application/atom+xml;profile=opds-catalog;kind=navigation";
    public const string AcquisitionType = "application/atom+xml;profile=opds-catalog;kind=acquisition";
    public const string OpenSearchType = "application/opensearchdescription+xml";

    /// <summary>The CBZ media type. Readers use it to decide they can open an acquisition link.</summary>
    public const string ComicBookType = "application/vnd.comicbook+zip";

    public const string AcquisitionRel = "http://opds-spec.org/acquisition";
    public const string OpenAccessRel = "http://opds-spec.org/acquisition/open-access";
    public const string ImageRel = "http://opds-spec.org/image";
    public const string ThumbnailRel = "http://opds-spec.org/image/thumbnail";
    public const string StreamRel = "http://vaemendis.net/opds-pse/stream";

    public static string ContentTypeFor(OpdsFeedKind kind) =>
        kind == OpdsFeedKind.Navigation ? NavigationType : AcquisitionType;

    public static string Render(OpdsFeed feed)
    {
        var root = new XElement(Atom + "feed",
            new XAttribute(XNamespace.Xmlns + "opds", Opds),
            new XAttribute(XNamespace.Xmlns + "pse", Pse),
            new XAttribute(XNamespace.Xmlns + "opensearch", OpenSearch),
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Atom + "id", feed.Id),
            new XElement(Atom + "title", feed.Title),
            new XElement(Atom + "updated", Timestamp(feed.Updated)));

        if (feed.TotalResults is { } total)
        {
            root.Add(new XElement(OpenSearch + "totalResults", total));
        }

        if (feed.ItemsPerPage is { } perPage)
        {
            root.Add(new XElement(OpenSearch + "itemsPerPage", perPage));
        }

        if (feed.StartIndex is { } start)
        {
            root.Add(new XElement(OpenSearch + "startIndex", start));
        }

        foreach (var link in feed.Links)
        {
            root.Add(LinkElement(link));
        }

        foreach (var entry in feed.Entries)
        {
            root.Add(EntryElement(entry));
        }

        return Serialize(new XDocument(root));
    }

    /// <summary>
    /// The OpenSearch description document the feed's <c>rel="search"</c> link points at. Readers
    /// fetch this to learn the query URL template; without it their search box stays disabled.
    /// </summary>
    public static string RenderOpenSearch(string shortName, string description, string urlTemplate)
    {
        XNamespace ns = "http://a9.com/-/spec/opensearch/1.1/";
        return Serialize(new XDocument(
            new XElement(ns + "OpenSearchDescription",
                new XElement(ns + "ShortName", shortName),
                new XElement(ns + "Description", description),
                new XElement(ns + "InputEncoding", "UTF-8"),
                new XElement(ns + "OutputEncoding", "UTF-8"),
                new XElement(ns + "Url",
                    new XAttribute("type", AcquisitionType),
                    new XAttribute("template", urlTemplate)))));
    }

    private static XElement LinkElement(OpdsLink link)
    {
        var element = new XElement(Atom + "link",
            new XAttribute("rel", link.Rel),
            new XAttribute("href", link.Href),
            new XAttribute("type", link.Type));

        if (link.Title is { Length: > 0 })
        {
            element.Add(new XAttribute("title", link.Title));
        }

        if (link.Length is { } length)
        {
            // Atom's own attribute for "how big is the thing at the other end", which is what
            // readers show as the download size.
            element.Add(new XAttribute("length", length));
        }

        return element;
    }

    private static XElement EntryElement(OpdsEntry entry)
    {
        var element = new XElement(Atom + "entry",
            new XElement(Atom + "id", entry.Id),
            new XElement(Atom + "title", entry.Title),
            new XElement(Atom + "updated", Timestamp(entry.Updated)));

        if (entry.Author is { Length: > 0 })
        {
            element.Add(new XElement(Atom + "author", new XElement(Atom + "name", entry.Author)));
        }

        if (entry.Content is { Length: > 0 })
        {
            // type="text", never "html": series overviews are scraped third-party prose and
            // handing markup to a reader app is not this feed's job.
            element.Add(new XElement(Atom + "content", new XAttribute("type", "text"), entry.Content));
        }

        foreach (var category in entry.Categories ?? [])
        {
            element.Add(new XElement(Atom + "category", new XAttribute("term", category)));
        }

        foreach (var link in entry.Links ?? [])
        {
            element.Add(LinkElement(link));
        }

        if (entry.Stream is { } stream)
        {
            var streamLink = new XElement(Atom + "link",
                new XAttribute("rel", StreamRel),
                // The placeholder must survive verbatim — it is the client that substitutes it.
                new XAttribute("href", stream.HrefTemplate),
                new XAttribute("type", "image/jpeg"),
                new XAttribute(Pse + "count", stream.Count));

            if (stream.LastRead is { } lastRead)
            {
                streamLink.Add(new XAttribute(Pse + "lastRead", lastRead));
            }

            if (stream.LastReadDate is { } lastReadDate)
            {
                streamLink.Add(new XAttribute(Pse + "lastReadDate", Timestamp(lastReadDate)));
            }

            element.Add(streamLink);
        }

        return element;
    }

    /// <summary>RFC 3339 UTC, which is what Atom requires and what readers parse.</summary>
    private static string Timestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Serialize(XDocument document)
    {
        var builder = new StringBuilder();

        // The declaration is written by hand. XmlWriter over a StringBuilder is a UTF-16 sink, so
        // letting it emit one produces encoding="utf-16" on a body the response then sends as UTF-8.
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
        using (var writer = XmlWriter.Create(builder, settings))
        {
            document.Save(writer);
        }

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + builder;
    }
}
