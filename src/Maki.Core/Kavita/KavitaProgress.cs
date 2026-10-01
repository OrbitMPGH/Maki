using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Scrobbling;

namespace Maki.Core.Kavita;

/// <summary>
/// Reading-progress computation over Kavita's volumes payload. A chapter/volume
/// counts as read only when every page is read; specials and sentinel-numbered
/// items are ignored. Kavita's series-level pagesRead aggregate is denormalized
/// and can be stale, so progress is always computed from chapter-level data.
/// <para>
/// One computation feeds both consumers on purpose: the tracker push takes the highest
/// chapter/volume from it, and the per-chapter read marks take the items themselves. They
/// used to read Kavita's payload separately, and the marks only understood numbered
/// chapters, so a library of volume archives scrobbled fine while nothing was marked read.
/// </para>
/// </summary>
public static class KavitaProgress
{
    /// <summary>Kavita marks specials/uncounted items with huge sentinel numbers.</summary>
    private const double Sentinel = 10000;

    /// <summary>Highest fully-read chapter/volume numbers across a series, and the items behind them.</summary>
    public record SeriesProgress(double MaxChapter, double MaxVolume, int ReadPages)
    {
        /// <summary>
        /// Fully read chapter numbers as inclusive ranges: a single chapter is <c>(n, n)</c>, a
        /// Kavita chapter range "1-5" is <c>(1, 5)</c>, and the chapters reached inside a
        /// multi-chapter volume archive are <c>(first chapter in it, last one read past)</c>.
        /// </summary>
        public IReadOnlyList<(decimal From, decimal To)> Chapters { get; init; } = [];

        /// <summary>Fully read volumes, as Kavita numbers them: <c>(3, 3)</c>, or <c>(1, 3)</c> for a "Vol 1-3" file.</summary>
        public IReadOnlyList<(int From, int To)> Volumes { get; init; } = [];

        public bool IsEmpty => Chapters.Count == 0 && Volumes.Count == 0;

        public bool CoversChapter(decimal number) => Chapters.Any(r => r.From <= number && number <= r.To);

        public bool CoversVolume(int from, int to) => Volumes.Contains((from, to));
    }

    // Id is last and defaulted so the existing positional construction in tests keeps compiling.
    // It is only needed to write progress back to Kavita, never to read it.
    public record KavitaChapterDto(
        [property: JsonPropertyName("number")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? Number,
        [property: JsonPropertyName("maxNumber")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? MaxNumber,
        [property: JsonPropertyName("pages")] int Pages,
        [property: JsonPropertyName("pagesRead")] int PagesRead,
        [property: JsonPropertyName("isSpecial")] bool IsSpecial,
        [property: JsonPropertyName("id")] int Id = 0);

    public record KavitaVolumeDto(
        [property: JsonPropertyName("number")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? Number,
        [property: JsonPropertyName("maxNumber")] [property: JsonConverter(typeof(LenientDoubleConverter))] double? MaxNumber,
        [property: JsonPropertyName("pages")] int Pages,
        [property: JsonPropertyName("pagesRead")] int PagesRead,
        [property: JsonPropertyName("chapters")] List<KavitaChapterDto>? Chapters,
        [property: JsonPropertyName("id")] int Id = 0);

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

    private static bool Countable(double? n) => n is > 0 and < Sentinel;

    /// <summary>
    /// Volume-only releases (chapters carry no usable number) still advance the
    /// volume counter: a volume is fully read when the sum of its chapters' read
    /// pages (or its own pagesRead) covers all of its pages.
    /// <para>
    /// <paramref name="boundariesByVolume"/> maps a volume number to the chapter start pages Maki
    /// scanned from its own multi-chapter archive for that volume (<see cref="Parsing.VolumeChapterScanner"/>).
    /// Kavita treats such an archive as one readable unit with one pagesRead counter; the
    /// boundaries map that counter back onto the chapters inside, so a half-read volume still
    /// counts the chapters already finished. A chapter counts only once the read-page count
    /// reaches the start of the next one (or the end of the archive for the last).
    /// </para>
    /// </summary>
    public static SeriesProgress Compute(
        IEnumerable<KavitaVolumeDto> volumes,
        IReadOnlyDictionary<int, VolumeChapterProgress.ChapterFileBoundaries>? boundariesByVolume = null)
    {
        var maxCh = 0.0;
        var maxVol = 0.0;
        var readPages = 0;
        var chapterRanges = new List<(decimal, decimal)>();
        var volumeRanges = new List<(int, int)>();

        foreach (var vol in volumes)
        {
            var chapters = vol.Chapters ?? [];
            var volPagesRead = 0;
            foreach (var ch in chapters)
            {
                volPagesRead += ch.PagesRead;
                if (ch.IsSpecial || ch.Pages <= 0 || ch.PagesRead < ch.Pages)
                {
                    continue;
                }

                var hi = ch.MaxNumber ?? ch.Number;
                if (!Countable(hi))
                {
                    continue;
                }

                var lo = Countable(ch.Number) && ch.Number <= hi ? ch.Number!.Value : hi!.Value;
                chapterRanges.Add(((decimal)lo, (decimal)hi!.Value));
                maxCh = Math.Max(maxCh, hi.Value);
            }

            if (chapters.Count == 0)
            {
                volPagesRead = vol.PagesRead;
            }

            readPages += volPagesRead;

            var vnum = vol.MaxNumber ?? vol.Number;
            if (!Countable(vnum))
            {
                continue;
            }

            if (vol.Pages > 0 && Math.Max(volPagesRead, vol.PagesRead) >= vol.Pages)
            {
                maxVol = Math.Max(maxVol, vnum!.Value);
                var vlo = Countable(vol.Number) && vol.Number <= vnum ? vol.Number!.Value : vnum!.Value;
                if (vlo == Math.Floor(vlo) && vnum == Math.Floor(vnum!.Value))
                {
                    volumeRanges.Add(((int)vlo, (int)vnum.Value));
                }
            }

            if (boundariesByVolume is null || vnum != Math.Floor(vnum!.Value) ||
                !boundariesByVolume.TryGetValue((int)vnum.Value, out var b) || b.Boundaries.Count == 0)
            {
                continue;
            }

            var totalPages = b.TotalPages > 0 ? b.TotalPages : vol.Pages;
            if (totalPages <= 0)
            {
                continue;
            }

            decimal? reached = null;
            for (var i = 0; i < b.Boundaries.Count; i++)
            {
                var endExclusive = i + 1 < b.Boundaries.Count ? b.Boundaries[i + 1].PageIndex : totalPages;
                if (volPagesRead >= endExclusive)
                {
                    reached = b.Boundaries[i].Chapter;
                }
            }

            if (reached is { } r)
            {
                chapterRanges.Add((b.Boundaries[0].Chapter, r));
                maxCh = Math.Max(maxCh, (double)r);
            }
        }

        return new SeriesProgress(maxCh, maxVol, readPages)
        {
            Chapters = chapterRanges,
            Volumes = volumeRanges,
        };
    }
}
