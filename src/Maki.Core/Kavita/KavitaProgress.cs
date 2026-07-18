using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Core.Kavita;

/// <summary>
/// Reading-progress computation over Kavita's volumes payload. A chapter/volume
/// counts as read only when every page is read; specials and sentinel-numbered
/// items are ignored. Kavita's series-level pagesRead aggregate is denormalized
/// and can be stale, so progress is always computed from chapter-level data.
/// </summary>
public static class KavitaProgress
{
    /// <summary>Kavita marks specials/uncounted items with huge sentinel numbers.</summary>
    private const double Sentinel = 10000;

    /// <summary>Highest fully-read chapter/volume numbers across a series.</summary>
    public record SeriesProgress(double MaxChapter, double MaxVolume, int ReadPages);

    public record KavitaChapterDto(
        [property: JsonPropertyName("number")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? Number,
        [property: JsonPropertyName("maxNumber")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? MaxNumber,
        [property: JsonPropertyName("pages")] int Pages,
        [property: JsonPropertyName("pagesRead")] int PagesRead,
        [property: JsonPropertyName("isSpecial")] bool IsSpecial);

    public record KavitaVolumeDto(
        [property: JsonPropertyName("number")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? Number,
        [property: JsonPropertyName("maxNumber")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? MaxNumber,
        [property: JsonPropertyName("pages")] int Pages,
        [property: JsonPropertyName("pagesRead")] int PagesRead,
        [property: JsonPropertyName("chapters")] List<KavitaChapterDto>? Chapters);

    /// <summary>Kavita sends chapter numbers as strings ("1", "1.5") and volume numbers as numbers.</summary>
    public class LenientDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetDouble(),
                JsonTokenType.String when double.TryParse(
                    reader.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) => value,
                _ => null,
            };

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value is { } v)
            {
                writer.WriteNumberValue(v);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    /// <summary>Countable chapter number, or null for specials/unnumbered chapters.</summary>
    private static double? ChapterNumber(KavitaChapterDto chapter)
    {
        if (chapter.IsSpecial)
        {
            return null;
        }

        var n = chapter.MaxNumber ?? chapter.Number;
        return n is > 0 and < Sentinel ? n : null;
    }

    private static double? VolumeNumber(KavitaVolumeDto volume)
    {
        var n = volume.MaxNumber ?? volume.Number;
        return n is > 0 and < Sentinel ? n : null;
    }

    /// <summary>
    /// Volume-only releases (chapters carry no usable number) still advance the
    /// volume counter: a volume is fully read when the sum of its chapters' read
    /// pages (or its own pagesRead) covers all of its pages.
    /// </summary>
    public static SeriesProgress Compute(IEnumerable<KavitaVolumeDto> volumes)
    {
        var maxCh = 0.0;
        var maxVol = 0.0;
        var readPages = 0;
        foreach (var vol in volumes)
        {
            var vnum = VolumeNumber(vol);
            var chapters = vol.Chapters ?? [];
            var volPagesRead = 0;
            foreach (var ch in chapters)
            {
                volPagesRead += ch.PagesRead;
                if (ChapterNumber(ch) is { } cnum && ch.Pages > 0 && ch.PagesRead >= ch.Pages)
                {
                    maxCh = Math.Max(maxCh, cnum);
                }
            }

            if (chapters.Count == 0)
            {
                volPagesRead = vol.PagesRead;
            }

            readPages += volPagesRead;
            var volFullyRead = vol.Pages > 0 && Math.Max(volPagesRead, vol.PagesRead) >= vol.Pages;
            if (vnum is { } v && volFullyRead)
            {
                maxVol = Math.Max(maxVol, v);
            }
        }

        return new SeriesProgress(maxCh, maxVol, readPages);
    }
}
