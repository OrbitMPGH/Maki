using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

/// <summary>
/// Which volume and chapter words <see cref="ReleaseNameParser"/> and <see cref="ReleaseTitleParser"/>
/// read besides the English ones, set from <c>parsing.languages</c>.
/// <para>
/// <see cref="Current"/> is a process-wide snapshot rather than a DI service because the parsers are
/// static and called from dozens of places, several of them static themselves. It is set at startup
/// and when the setting is saved; tests pass a profile explicitly instead.
/// </para>
/// </summary>
public sealed class NameParsingProfile
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // A word must start a token, so "Robot 2" holds no "t" and "Rock Band 3" holds no "band" either.
    private const string WordStart = @"(?<![\p{L}\p{N}])";

    // The single letters are stricter still: an apostrophe in front rules out "Don't 2".
    private const string LetterStart = @"(?<![\p{L}\p{N}'’])";

    private const string Integer = "((?>[0-9]+))";
    private const string Number = @"((?>[0-9]+(?:\.[0-9]+)?))";
    private const string NoLetterAfter = @"(?!\p{L})";

    public static NameParsingProfile Default { get; } = new([]);

    /// <summary>Every language on: what the release search uses to say a release would parse with another one.</summary>
    public static NameParsingProfile AllLanguages { get; } = new(ReleaseMarkers.Languages);

    private static NameParsingProfile current = Default;

    public static NameParsingProfile Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value);
    }

    private readonly Regex? volume;
    private readonly Regex? weakVolume;
    private readonly Regex? chapter;
    private readonly Regex? volumeSuffix;
    private readonly Regex? chapterSuffix;

    /// <summary>Unknown codes and English are ignored, so a stale or hand-edited setting still loads.</summary>
    public NameParsingProfile(IEnumerable<string> languages)
    {
        var wanted = languages.Select(ReleaseMarkers.Language).OfType<string>().ToHashSet();
        Languages = ReleaseMarkers.Languages.Where(wanted.Contains).ToList();
        Fingerprint = string.Join(',', Languages);

        var words = ReleaseMarkers.Words.Where(w => w.Languages.Any(wanted.Contains)).ToList();
        string? Alternation(MarkerKind kind) =>
            words.Where(w => w.Kind == kind).Select(w => w.Pattern).ToList() is { Count: > 0 } found
                ? string.Join('|', found)
                : null;

        if (Alternation(MarkerKind.Volume) is { } v)
        {
            // The same range rule as the English pattern: a bare hyphen, or a spaced one when the end
            // repeats the marker or closes the name.
            volume = new Regex(
                $@"{WordStart}(?:{v})\.?[\s_]*{Integer}(?:(?:-|\s+-\s*(?=(?:{v})|[0-9]+\s*(?:$|[(\[+,]|(?:complete|end)\b)))(?:(?:{v})\.?[\s_]*)?{Integer})?{NoLetterAfter}",
                Options);
        }

        if (Alternation(MarkerKind.WeakVolume) is { } weak)
        {
            weakVolume = new Regex($@"{LetterStart}(?:{weak})\.?{Integer}(?:-(?:(?:{weak})\.?)?{Integer})?{NoLetterAfter}", Options);
        }

        if (Alternation(MarkerKind.Chapter) is { } c)
        {
            chapter = new Regex($@"{WordStart}(?:{c})\.?[\s_]*{Number}(?:-(?:(?:{c})\.?[\s_]*)?{Number})?{NoLetterAfter}", Options);
        }

        // No letter test in front: Japanese and Chinese run the title straight into the number
        // ("ワンピース12巻"), and 第 is a letter itself. "全12巻" is a complete set, volumes 1 to 12.
        if (Alternation(MarkerKind.VolumeSuffix) is { } vs)
        {
            volumeSuffix = new Regex($@"(?<!\p{{N}})(?<all>全)?(?:第|제)?\s*{Integer}(?:\s*[-~〜]\s*{Integer})?\s*(?:{vs})", Options);
        }

        if (Alternation(MarkerKind.ChapterSuffix) is { } cs)
        {
            chapterSuffix = new Regex($@"(?<![\p{{N}}.])(?:第|제)?\s*{Number}(?:\s*[-~〜]\s*{Number})?\s*(?:{cs})", Options);
        }
    }

    public static NameParsingProfile FromSetting(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? Default : new NameParsingProfile(csv.Split(','));

    /// <summary>The enabled languages, in picker order.</summary>
    public IReadOnlyList<string> Languages { get; }

    /// <summary>Equal for two profiles that parse alike, so a plan can tell it was built under other rules.</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Volume markers in order of appearance. A single letter only counts when no full word is
    /// there, so "Terminator T2 v05" is volume 5 rather than a span from 2 to 5.
    /// </summary>
    internal IReadOnlyList<Match> VolumeMatches(string text)
    {
        var strong = InOrder(text, ReleaseNameParser.VolumePattern(), volume, volumeSuffix);
        return strong.Count > 0 || weakVolume is null ? strong : weakVolume.Matches(text).ToList();
    }

    internal IReadOnlyList<Match> ChapterMatches(string text) =>
        InOrder(text, ReleaseNameParser.ChapterPattern(), chapter, chapterSuffix);

    private static List<Match> InOrder(string text, params Regex?[] patterns) =>
        patterns.OfType<Regex>()
            .SelectMany(p => p.Matches(text))
            .OrderBy(m => m.Index)
            .ToList();
}
