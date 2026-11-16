using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Core.Xml;

namespace Maki.Core.ComicInfo;

public static class ComicInfoBuilder
{
    /// <param name="userTags">The series' user tag labels, written to SeriesGroup, where Kavita and Komga build collections from.</param>
    /// <param name="provenance">Source, URL and quality tier for Notes.</param>
    /// <param name="widePages">Per page, whether it is a spread, for the Pages block.</param>
    public static ComicInfo Build(
        Series series, Chapter chapter, int pageCount, string? group = null, IEnumerable<string>? userTags = null,
        ComicInfoProvenance? provenance = null, IReadOnlyList<bool>? widePages = null)
    {
        return new ComicInfo
        {
            Series = series.Title,
            LocalizedSeries = LocalizedSeriesFor(series, chapter.Language),
            Title = !string.IsNullOrWhiteSpace(chapter.Title)
                ? chapter.Title
                : chapter.Number is decimal n
                    ? $"Chapter {n.ToString("0.###", CultureInfo.InvariantCulture)}"
                    : series.Title,
            Number = chapter.Number?.ToString("0.###", CultureInfo.InvariantCulture),
            VolumeSerialized = chapter.Volume?.ToString(CultureInfo.InvariantCulture),
            // Kavita uses Count to compute completion; only meaningful once the series is done.
            CountSerialized = series.Status == SeriesStatus.Completed
                ? series.TotalChapters?.ToString(CultureInfo.InvariantCulture)
                : null,
            Summary = series.Overview,
            Notes = ComicInfoPages.NotesFor(provenance),
            Year = chapter.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture),
            Month = chapter.ReleaseDate?.Month.ToString(CultureInfo.InvariantCulture),
            Day = chapter.ReleaseDate?.Day.ToString(CultureInfo.InvariantCulture),
            Writer = JoinList(series.AuthorStory),
            Penciller = JoinList(series.AuthorArt),
            Translator = ScanInformationFor(group),
            Publisher = JoinList(series.Publisher),
            Genre = JoinList(series.Genres),
            Tags = JoinList(series.Tags),
            SeriesGroup = SeriesGroupFor(userTags),
            Web = SeriesWebLinks.Joined(series),
            LanguageISO = chapter.Language,
            Manga = MangaFor(series.Type) ?? DefaultManga,
            AgeRating = AgeRatingFor(series.ContentRating),
            ScanInformation = ScanInformationFor(group),
            PageCount = pageCount.ToString(CultureInfo.InvariantCulture),
            Pages = ComicInfoPages.Build(pageCount, widePages)
        };
    }

    /// <summary>
    /// A ComicInfo list field: each name trimmed, inner runs of whitespace collapsed, empty entries
    /// dropped, joined with ", ". Provider data carries stray spaces ("Panini Manga México , Devir"),
    /// and Kavita splits on the comma and keeps whatever surrounds it.
    /// </summary>
    internal static string? JoinList(IEnumerable<string?> items)
    {
        var names = items
            .Select(item => string.Join(' ', (item ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(name => name.Length > 0)
            .ToList();
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    internal const string DefaultManga = "YesAndRightToLeft";

    /// <summary>
    /// ComicInfo's reading-direction hint for a series type: manga is right-to-left, manhwa and
    /// manhua are manga-style left-to-right strips, OEL comics are not manga. Null for an
    /// unknown type, which callers resolve themselves.
    /// </summary>
    internal static string? MangaFor(string? seriesType) => seriesType switch
    {
        SeriesTypes.Manga => DefaultManga,
        SeriesTypes.Manhwa or SeriesTypes.Manhua => "Yes",
        SeriesTypes.Oel or SeriesTypes.Other => "No",
        _ => null
    };

    /// <summary>
    /// ComicInfo's AgeRating for Maki's content rating. The vocabulary says nothing about violence,
    /// so "safe" maps to Teen rather than Everyone, and each step up sits one band higher than the
    /// sexual content alone would put it. Null for an unknown or absent rating.
    /// </summary>
    internal static string? AgeRatingFor(string? contentRating) => contentRating?.Trim().ToLowerInvariant() switch
    {
        "safe" => "Teen",
        "suggestive" => "Mature 17+",
        "erotica" => "Adults Only 18+",
        "pornographic" => "X18+",
        _ => null
    };

    /// <summary>User tag labels as ComicInfo's SeriesGroup, sorted so the same tags always write the same text.</summary>
    internal static string? SeriesGroupFor(IEnumerable<string>? userTags) =>
        userTags is null ? null : JoinList(userTags.Select(t => t.Trim()).Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>The scanlation group as ComicInfo's ScanInformation, or null when there is none.</summary>
    internal static string? ScanInformationFor(string? group) =>
        string.IsNullOrWhiteSpace(group) ? null : group.Trim();

    /// <summary>A stored comma-separated list, normalized the same way.</summary>
    internal static string? JoinList(string? joined) => joined is null ? null : JoinList(joined.Split(','));

    /// <summary>
    /// Kavita's localized name for the series: the alt title written in this chapter's language,
    /// falling back to the native-script title.
    /// <para>
    /// <see cref="ComicInfo.Series"/> is <see cref="Series.Title"/>, which is the provider's English
    /// name — so for an English chapter the language-matched alt title is just <em>another</em>
    /// English name, picked arbitrarily from however many the provider listed. The native title is
    /// the pairing Kavita is usually given (English or romanized name + original name), so English
    /// goes straight to it.
    /// </para>
    /// </summary>
    internal static string? LocalizedSeriesFor(Series series, string language) =>
        language.Equals(SourceLanguages.Default, StringComparison.OrdinalIgnoreCase)
            ? series.OriginalTitle
            : LocalizedTitle.Pick(series.AltTitles, [language]) ?? series.OriginalTitle;

    /// <summary>Lenient parse of an existing ComicInfo.xml; null when malformed.</summary>
    public static ComicInfo? Deserialize(Stream stream) =>
        Deserialize(XmlReader.Create(stream, ReaderSettings));

    /// <summary>
    /// Parses text that was already decoded, so a declared <c>encoding="utf-16"</c> on a file that
    /// was read with its BOM (or re-saved as UTF-8) does not make the reader reject it.
    /// </summary>
    public static ComicInfo? Deserialize(TextReader text) =>
        Deserialize(XmlReader.Create(text, ReaderSettings));

    private static readonly XmlReaderSettings ReaderSettings = new() { DtdProcessing = DtdProcessing.Ignore };

    private static ComicInfo? Deserialize(XmlReader xml)
    {
        try
        {
            using (xml)
            {
                return new XmlSerializer(typeof(ComicInfo)).Deserialize(xml) as ComicInfo;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly PropertyInfo[] TextProperties = typeof(ComicInfo)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite)
        .ToArray();

    // XmlAnyElement matches names case-sensitively, so "<summary>" in an imported file lands in
    // Unmodelled; written back beside the modelled <Summary> it would make a duplicate field. Its
    // text moves into the modelled property when that is empty, so the value is not lost.
    private static readonly Dictionary<string, PropertyInfo> TextByElement = TextProperties.ToDictionary(
        p => p.GetCustomAttribute<XmlElementAttribute>()?.ElementName ?? p.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ModelledElements = new(
        typeof(ComicInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<XmlAnyElementAttribute>() is null &&
                        p.GetCustomAttribute<XmlAnyAttributeAttribute>() is null)
            .Select(p => p.GetCustomAttribute<XmlElementAttribute>()?.ElementName ?? p.Name),
        StringComparer.OrdinalIgnoreCase);

    /// <remarks>Strips characters XML cannot carry from every text field of <paramref name="info"/> first.</remarks>
    public static string Serialize(ComicInfo info)
    {
        foreach (var element in info.Unmodelled ?? [])
        {
            if (TextByElement.TryGetValue(element.LocalName, out var property) &&
                string.IsNullOrWhiteSpace((string?)property.GetValue(info)) &&
                !string.IsNullOrWhiteSpace(element.InnerText))
            {
                property.SetValue(info, element.InnerText.Trim());
            }
        }

        info.Unmodelled = info.Unmodelled?.Where(e => !ModelledElements.Contains(e.LocalName)).ToArray();
        foreach (var property in TextProperties)
        {
            property.SetValue(info, XmlChars.Strip((string?)property.GetValue(info)));
        }

        var serializer = new XmlSerializer(typeof(ComicInfo));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false)
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            InSchemaOrder(serializer, info).Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The ComicInfo schema's element sequence (v2.0, plus the v2.1 additions Maki writes in their
    /// v2.1 places). XmlSerializer writes in declaration order and puts every unmodelled element
    /// where <see cref="ComicInfo.Unmodelled"/> sits, so the order is restored after it.
    /// </summary>
    internal static readonly string[] SchemaOrder =
    [
        "Title", "Series", "LocalizedSeries", "Number", "Count", "Volume", "AlternateSeries", "AlternateNumber",
        "AlternateCount", "Summary", "Notes", "Year", "Month", "Day", "Writer", "Penciller", "Inker",
        "Colorist", "Letterer", "CoverArtist", "Editor", "Translator", "Publisher", "Imprint", "Genre", "Tags",
        "Web", "PageCount", "LanguageISO", "Format", "BlackAndWhite", "Manga", "Characters", "Teams",
        "Locations", "ScanInformation", "StoryArc", "StoryArcNumber", "SeriesGroup", "AgeRating", "Pages",
        "CommunityRating", "MainCharacterOrTeam", "Review", "GTIN",
    ];

    private static readonly Dictionary<string, int> SchemaIndex = SchemaOrder
        .Select((name, i) => (name, i))
        .ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);

    /// <summary>
    /// The document as an <see cref="XDocument"/> with the root's children stably sorted into
    /// <see cref="SchemaOrder"/>; an element the schema does not name keeps its relative place at the end.
    /// </summary>
    private static XDocument InSchemaOrder(XmlSerializer serializer, ComicInfo info)
    {
        var document = new XDocument();
        using (var writer = document.CreateWriter())
        {
            serializer.Serialize(writer, info);
        }

        var root = document.Root!;
        var ordered = root.Elements()
            .Select((element, i) => (element, i))
            .OrderBy(x => SchemaIndex.GetValueOrDefault(x.element.Name.LocalName, int.MaxValue))
            .ThenBy(x => x.i)
            .Select(x => x.element)
            .ToList();
        foreach (var element in ordered)
        {
            element.Remove();
        }

        root.Add(ordered);
        return document;
    }
}
