using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Mangarr.Core.Entities;

namespace Mangarr.Core.ComicInfo;

public static class ComicInfoBuilder
{
    public static ComicInfo Build(Series series, Chapter chapter, int pageCount)
    {
        return new ComicInfo
        {
            Series = series.Title,
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
            Genre = series.Genres.Count > 0 ? string.Join(", ", series.Genres) : null,
            Tags = series.Tags.Count > 0 ? string.Join(", ", series.Tags) : null,
            Web = series.MangaBakaId is int id ? $"https://mangabaka.org/{id}" : null,
            LanguageISO = chapter.Language,
            Manga = "YesAndRightToLeft",
            PageCount = pageCount.ToString(CultureInfo.InvariantCulture)
        };
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
