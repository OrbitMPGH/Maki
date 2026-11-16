using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Serialization;
using Maki.Core.Images;
using Maki.Core.Quality;

namespace Maki.Core.ComicInfo;

/// <summary>
/// One <c>&lt;Page&gt;</c> of the ComicInfo <c>Pages</c> block. Every attribute is a string so a file
/// another tool wrote with <c>DoublePage="True"</c> or a blank <c>Image</c> still parses; a typed
/// attribute that failed would make the whole document unreadable and lose every field in it.
/// </summary>
public class ComicPageInfo
{
    [XmlAttribute]
    public string? Image { get; set; }

    [XmlAttribute]
    public string? Type { get; set; }

    [XmlAttribute]
    public string? DoublePage { get; set; }

    [XmlAnyAttribute]
    public XmlAttribute[]? Other { get; set; }
}

/// <summary>Where a file came from, for ComicInfo's Notes.</summary>
/// <param name="Source">The source's display name, or null for a file Maki did not download.</param>
/// <param name="Url">The chapter's page on that source, else the series page.</param>
public sealed record ComicInfoProvenance(string? Source, string? Url, QualityTier Tier);

public static class ComicInfoPages
{
    /// <summary>Bytes read from the start of an archived page to find its size from the header.</summary>
    private const int HeaderBytes = 64 * 1024;

    /// <summary>
    /// The Pages block: every page by its index in reading order, the first marked as the front
    /// cover and each page wider than it is tall marked as a double page. Null for no pages.
    /// </summary>
    /// <param name="widePages">Per page, whether it is a spread; shorter than the page count or null when unknown.</param>
    public static List<ComicPageInfo>? Build(int pageCount, IReadOnlyList<bool>? widePages)
    {
        if (pageCount <= 0)
        {
            return null;
        }

        return Enumerable.Range(0, pageCount)
            .Select(i => new ComicPageInfo
            {
                Image = i.ToString(CultureInfo.InvariantCulture),
                Type = i == 0 ? "FrontCover" : null,
                DoublePage = widePages is not null && i < widePages.Count && widePages[i] ? "true" : null,
            })
            .ToList();
    }

    /// <summary>Whether each page file is wider than tall, from its header. An unreadable page counts as not wide.</summary>
    public static List<bool> WideFromFiles(IReadOnlyList<string> pageFiles) =>
        pageFiles.Select(file =>
        {
            try
            {
                var header = ImageCodec.IdentifyFile(file);
                return header.Width > header.Height;
            }
            catch (Exception)
            {
                return false;
            }
        }).ToList();

    /// <summary>
    /// The same for pages already in an archive, reading only the start of each entry: the size sits
    /// in the header, so a whole volume costs a few megabytes rather than every page's bytes.
    /// </summary>
    public static List<bool> WideFromArchive(ZipArchive archive, IReadOnlyList<string> pageNames) =>
        pageNames.Select(name =>
        {
            try
            {
                if (archive.GetEntry(name) is not { } entry)
                {
                    return false;
                }

                var buffer = new byte[(int)Math.Min(HeaderBytes, entry.Length)];
                using var stream = entry.Open();
                var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                var header = ImageCodec.TryIdentify(buffer[..read]);
                return header is { } h && h.Width > h.Height;
            }
            catch (Exception)
            {
                return false;
            }
        }).ToList();

    /// <summary>
    /// ComicInfo's Notes for a file: the source, the URL and the quality tier, each only when known.
    /// Fixed English labels: the file is read by other programs, in no particular user's language.
    /// </summary>
    public static string? NotesFor(ComicInfoProvenance? provenance)
    {
        if (provenance is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(provenance.Source))
        {
            parts.Add($"Source: {provenance.Source.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(provenance.Url))
        {
            parts.Add($"URL: {provenance.Url.Trim()}");
        }

        if (provenance.Tier != QualityTier.Unknown)
        {
            parts.Add($"Quality: {provenance.Tier}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
