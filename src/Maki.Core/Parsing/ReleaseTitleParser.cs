using System.Globalization;
using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

public sealed record NumberRange(decimal Start, decimal End);

public sealed record ReleaseSpan(NumberRange? Volumes, IReadOnlyList<NumberRange> ChapterSegments)
{
    public bool IsEmpty => Volumes is null && ChapterSegments.Count == 0;
}

public sealed record ParsedReleaseTitle(
    IReadOnlyList<string> TitleCandidates,
    ReleaseSpan Span,
    IReadOnlyList<string> Tags,
    bool IsDigital,
    string? Group,
    int? Year)
{
    /// <summary>
    /// A lone number ending an otherwise spanless title ("Ayakashi Triangle 133"). The parser can't tell
    /// a chapter from a title that ends in a number ("Mob Psycho 100"), so the caller decides.
    /// </summary>
    public decimal? TrailingNumber { get; init; }

    /// <summary>The title candidate that carried <see cref="TrailingNumber"/>, with the number removed.</summary>
    public string? TitleWithoutTrailingNumber { get; init; }
}

/// <summary>
/// Parses a whole-torrent title such as
/// "Aishiteru Game wo Owarasetai | I Want to End This Love Game v01-06 + 049.1-057 (2023-2025) (Digital) (1r0n)"
/// into its title candidates, the volumes and chapters it spans, and its tags. Per-file names inside
/// a torrent go through <see cref="ReleaseNameParser"/> instead.
/// </summary>
public static partial class ReleaseTitleParser
{
    [GeneratedRegex(@"\s*\|\s*|\s+/\s+|\s+aka\s+", RegexOptions.IgnoreCase)]
    private static partial Regex TitleSeparators();

    // A number or range with no marker. Only a span when it follows "+" or a volume span; the
    // lookarounds keep it off the digits of a resolution ("1920x1080") or a longer word.
    [GeneratedRegex(@"(?<![\w.])(\d+(?:\.\d+)?)(?:-(\d+(?:\.\d+)?))?(?![\w.])")]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"^(?:19|20)\d{2}$")]
    private static partial Regex YearLike();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static readonly char[] EdgePunctuation = [' ', '-', (char)0x2013, (char)0x2014, '_', '.', ',', ':', ';', '+', '~', '|', '/'];

    private enum TokenKind { Volume, Chapter }

    private sealed record Token(int Start, int End, TokenKind Kind, NumberRange Range);

    public static ParsedReleaseTitle Parse(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new ParsedReleaseTitle([], new ReleaseSpan(null, []), [], false, null, null);
        }

        var tags = ReleaseNameParser.ExtractTags(title);
        var remainder = ReleaseNameParser.TagGroups().Replace(title, " ").Replace('_', ' ');

        var candidates = new List<string>();
        var volumes = new List<NumberRange>();
        var chapters = new List<NumberRange>();
        var halves = TitleSeparators().Split(remainder);
        decimal? trailingNumber = null;
        string? withoutTrailing = null;
        for (var i = 0; i < halves.Length; i++)
        {
            var half = halves[i];
            var tokens = SpanTokens(half);
            if (i == halves.Length - 1 && TrailingBareNumber(half, tokens) is { } trailing)
            {
                // A trailing range is a chapter span only when the title names no other span: after
                // "v01-10", "Extras 1-3" counts something else.
                if (trailing.Groups[2].Success)
                {
                    if (tokens.Count == 0 && volumes.Count == 0 && chapters.Count == 0 &&
                        TrailingRange(trailing) is { } range)
                    {
                        tokens.Add(new Token(trailing.Index, trailing.Index + trailing.Length, TokenKind.Chapter, range));
                    }
                }
                else if (tokens.Count == 0 && IsChapterLike(trailing.Groups[1].Value))
                {
                    var stripped = Clean(half[..trailing.Index] + " " + half[(trailing.Index + trailing.Length)..]);
                    if (stripped.Length > 0)
                    {
                        trailingNumber = decimal.Parse(trailing.Groups[1].Value, CultureInfo.InvariantCulture);
                        withoutTrailing = stripped;
                    }
                }
            }

            volumes.AddRange(tokens.Where(t => t.Kind == TokenKind.Volume).Select(t => t.Range));
            chapters.AddRange(tokens.Where(t => t.Kind == TokenKind.Chapter).Select(t => t.Range));

            var candidate = Clean(RemoveTokens(half, tokens));
            if (candidate.Length > 0 && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        if (volumes.Count > 0 || chapters.Count > 0)
        {
            trailingNumber = null;
            withoutTrailing = null;
        }

        if (candidates.Count == 0)
        {
            candidates.Add(Clean(title.Replace('_', ' ')));
        }

        var span = new ReleaseSpan(
            volumes.Count == 0 ? null : new NumberRange(volumes.Min(v => v.Start), volumes.Max(v => v.End)),
            chapters);

        return new ParsedReleaseTitle(candidates, span, tags, ReleaseTags.IsDigital(tags),
            ReleaseTags.Group(tags), ReleaseTags.Year(tags))
        {
            TrailingNumber = trailingNumber,
            TitleWithoutTrailingNumber = withoutTrailing
        };
    }

    /// <summary>
    /// The bare number or range that ends the half, right before the tags. Null when it is part of a
    /// span token already, directly follows one, or is all the half has.
    /// </summary>
    private static Match? TrailingBareNumber(string half, List<Token> tokens)
    {
        var last = BareNumber().Matches(half).LastOrDefault();
        if (last is null) return null;

        var start = last.Index;
        var end = last.Index + last.Length;
        if (half[end..].Trim(EdgePunctuation).Length > 0) return null;
        if (tokens.Any(t => start < t.End && end > t.Start)) return null;
        if (tokens.LastOrDefault(t => t.End <= start) is { } previous && half[previous.End..start].Trim().Length == 0) return null;
        if (Clean(RemoveTokens(half[..start], tokens.Where(t => t.End <= start).ToList())).Length == 0) return null;

        return last;
    }

    private static NumberRange? TrailingRange(Match bare)
    {
        var first = bare.Groups[1].Value;
        var last = bare.Groups[2].Value;
        if (YearLike().IsMatch(first) || YearLike().IsMatch(last)) return null;

        var start = decimal.Parse(first, CultureInfo.InvariantCulture);
        var end = decimal.Parse(last, CultureInfo.InvariantCulture);
        return end >= start ? new NumberRange(start, end) : null;
    }

    private static bool IsChapterLike(string number) =>
        number.Split('.')[0].Length <= 4 && !YearLike().IsMatch(number);

    private static List<Token> SpanTokens(string half)
    {
        var marked = ReleaseNameParser.VolumePattern().Matches(half)
            .Select(m =>
            {
                var (start, end) = ReleaseNameParser.VolumeRange(m);
                return new Token(m.Index, m.Index + m.Length, TokenKind.Volume,
                    new NumberRange(start, end is { } e && e > start ? e : start));
            })
            .Concat(ReleaseNameParser.ChapterPattern().Matches(half).Select(m =>
            {
                var (start, end) = ReleaseNameParser.ChapterRange(m);
                return new Token(m.Index, m.Index + m.Length, TokenKind.Chapter, new NumberRange(start, end ?? start));
            }))
            .OrderBy(t => t.Start);

        var tokens = new List<Token>();
        foreach (var token in marked)
        {
            if (tokens.Count == 0 || token.Start >= tokens[^1].End) tokens.Add(token);
        }

        foreach (Match bare in BareNumber().Matches(half))
        {
            var start = bare.Index;
            var end = bare.Index + bare.Length;
            if (tokens.Any(t => start < t.End && end > t.Start) || IsYear(bare)) continue;

            var previous = tokens.LastOrDefault(t => t.End <= start);
            if (previous is null) continue;

            var gap = half[previous.End..start].Trim();
            if (gap != "+" && !(gap.Length == 0 && previous.Kind == TokenKind.Volume)) continue;

            var first = decimal.Parse(bare.Groups[1].Value, CultureInfo.InvariantCulture);
            var last = bare.Groups[2].Success
                ? decimal.Parse(bare.Groups[2].Value, CultureInfo.InvariantCulture)
                : first;
            tokens.Add(new Token(start, end, TokenKind.Chapter, new NumberRange(first, last > first ? last : first)));
            tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return tokens;
    }

    private static bool IsYear(Match bare) =>
        YearLike().IsMatch(bare.Groups[1].Value) &&
        (!bare.Groups[2].Success || YearLike().IsMatch(bare.Groups[2].Value));

    /// <summary>The half with every span token and the "+" joining two of them blanked out.</summary>
    private static string RemoveTokens(string half, List<Token> tokens)
    {
        if (tokens.Count == 0) return half;

        var chars = half.ToCharArray();
        for (var i = 0; i < tokens.Count; i++)
        {
            var from = tokens[i].Start;
            if (i > 0 && half[tokens[i - 1].End..from].Trim() is "+" or "")
            {
                from = tokens[i - 1].End;
            }

            for (var c = from; c < tokens[i].End; c++) chars[c] = ' ';
        }

        return new string(chars);
    }

    private static string Clean(string text) =>
        Whitespace().Replace(text, " ").Trim(EdgePunctuation);
}
