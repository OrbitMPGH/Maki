using System.Globalization;
using System.Text.RegularExpressions;

namespace Maki.Core.Recommendations;

public enum AnimeSpanKind { Season, Film, Other }

/// <param name="Label">The start marker's label.</param>
/// <param name="To">Inclusive. Null when the span is open-ended (a season still airing has no end marker).</param>
/// <param name="EndLabel">The paired end marker's label, e.g. "S3P2" closing an "S3P1" start. Null when the
/// span is open-ended or the end carried no label of its own.</param>
public record AnimeSpan(
    string Label, decimal? From, decimal? To, bool OpenEnded, AnimeSpanKind Kind, string? EndLabel = null);

/// <summary>
/// C# port of <c>frontend/src/lib/animeCoverage.ts</c>: turns MangaBaka's free-text AnimeStart and
/// AnimeEnd fields, e.g. "Vol 1, Chap 1 (S1) / Vol 31, Chap 270 (Film + OVA) / Vol 35, Chap 315 (S2)",
/// into chapter spans. Keep the two in step. Unlike the TS version there is no last-chapter
/// argument and no lane capping: an open-ended span has a null <see cref="AnimeSpan.To"/>.
/// </summary>
public static class AnimeCoverage
{
    private static readonly Regex LabelledMarker = new(
        @"Chap\s*([0-9]+(?:\.[0-9]+)?)[^()/]*\(([^)]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BareMarker = new(
        @"Chap\s*([0-9]+(?:\.[0-9]+)?)[^()/]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FilmLabel = new(
        @"\b(film|movie|ova|ona|special|specials)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SeasonLabel = new(
        @"(^|\b)(s[0-9]|season|shippuden|part\s*[0-9]|cour)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private record Marker(decimal Number, string Label, bool Labelled);

    private sealed class EndMarker(decimal number, string label, bool labelled)
    {
        public decimal Number { get; } = number;
        public string Label { get; } = label;
        public bool Labelled { get; } = labelled;
        public bool Used { get; set; }
    }

    public static IReadOnlyList<AnimeSpan> Parse(string? animeStart, string? animeEnd)
    {
        var starts = ParseMarkers(animeStart).OrderBy(m => m.Number).ToList();
        var ends = ParseMarkers(animeEnd)
            .OrderBy(m => m.Number)
            .Select(m => new EndMarker(m.Number, m.Label, m.Labelled))
            .ToList();

        var spans = new List<AnimeSpan>();
        foreach (var start in starts)
        {
            var end = FindEnd(ends, start, matchLabel: true) ?? FindEnd(ends, start, matchLabel: false);
            if (end is not null)
            {
                end.Used = true;
                if (end.Number <= start.Number) continue;
            }

            spans.Add(new AnimeSpan(start.Label, start.Number, end?.Number, end is null, KindOf(start.Label),
                end is { Labelled: true } ? end.Label : null));
        }

        return spans
            .OrderBy(s => s.From)
            .ThenByDescending(s => s.To ?? decimal.MaxValue)
            .ToList();
    }

    public static AnimeSpanKind KindOf(string label)
    {
        var s = label.ToLowerInvariant();
        if (FilmLabel.IsMatch(s)) return AnimeSpanKind.Film;
        if (SeasonLabel.IsMatch(s)) return AnimeSpanKind.Season;
        return AnimeSpanKind.Other;
    }

    private static List<Marker> ParseMarkers(string? text)
    {
        var markers = new List<Marker>();
        if (string.IsNullOrEmpty(text)) return markers;

        foreach (Match match in LabelledMarker.Matches(text))
        {
            if (TryNumber(match.Groups[1].Value, out var number))
                markers.Add(new Marker(number, match.Groups[2].Value.Trim(), Labelled: true));
        }

        if (markers.Count == 0)
        {
            var bare = BareMarker.Match(text);
            if (bare.Success && TryNumber(bare.Groups[1].Value, out var number))
                markers.Add(new Marker(number, "S1", Labelled: false));
        }

        return markers;
    }

    private static EndMarker? FindEnd(List<EndMarker> ends, Marker start, bool matchLabel)
    {
        var label = Normalize(start.Label);
        foreach (var end in ends)
        {
            if (end.Used || end.Number < start.Number) continue;
            if (matchLabel && Normalize(end.Label) != label) continue;
            return end;
        }
        return null;
    }

    private static string Normalize(string label) => label.Trim().ToLowerInvariant();

    private static bool TryNumber(string text, out decimal number) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
}
