using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Core.ComicInfo;

public static class ComicInfoBuilder
{
    public static ComicInfo Build(Series series, Chapter chapter, int pageCount)
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
            Year = chapter.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture),
            Month = chapter.ReleaseDate?.Month.ToString(CultureInfo.InvariantCulture),
            Day = chapter.ReleaseDate?.Day.ToString(CultureInfo.InvariantCulture),
            Writer = series.AuthorStory,
            Penciller = series.AuthorArt,
            Publisher = series.Publisher,
            Genre = series.Genres.Count > 0 ? string.Join(", ", series.Genres) : null,
            Tags = series.Tags.Count > 0 ? string.Join(", ", series.Tags) : null,
            Web = SeriesWebLinks.Joined(series),
            LanguageISO = chapter.Language,
            Manga = "YesAndRightToLeft",
            PageCount = pageCount.ToString(CultureInfo.InvariantCulture)
        };
    }

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
    public static ComicInfo? Deserialize(Stream stream)
    {
        try
        {
            var serializer = new XmlSerializer(typeof(ComicInfo));
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            return serializer.Deserialize(reader) as ComicInfo;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Serialize(ComicInfo info)
    {
        var serializer = new XmlSerializer(typeof(ComicInfo));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false)
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            serializer.Serialize(writer, info);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
