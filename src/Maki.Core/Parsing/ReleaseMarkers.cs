using Maki.Core.Localization;

namespace Maki.Core.Parsing;

internal enum MarkerKind
{
    Volume,

    /// <summary>A single letter ("T29"). Read only when the name carries no full volume word.</summary>
    WeakVolume,

    Chapter,

    /// <summary>Number first, marker after: "第12巻", "3권".</summary>
    VolumeSuffix,

    ChapterSuffix
}

/// <param name="Pattern">Regex fragment for the word itself; a trailing <c>\.</c> makes the dot required.</param>
/// <param name="Display">How the settings card shows the word.</param>
/// <param name="Languages">Every language that uses it. The word is on when any of them is.</param>
internal sealed record MarkerWord(string Pattern, string Display, MarkerKind Kind, string[] Languages);

/// <summary>
/// Volume and chapter words in the languages Maki ships, beyond the English ones
/// <see cref="ReleaseNameParser"/> always reads. Each language is switched on in settings
/// (<see cref="NameParsingProfile"/>), so a library that never sees them parses exactly as before.
/// </summary>
public static class ReleaseMarkers
{
    internal static readonly MarkerWord[] Words =
    [
        new("tome", "Tome", MarkerKind.Volume, ["fr"]),
        new("t", "T", MarkerKind.WeakVolume, ["fr", "pl"]),
        new("chapitre", "Chapitre", MarkerKind.Chapter, ["fr"]),
        new("chap", "Chap.", MarkerKind.Chapter, ["fr"]),

        new("tomo", "Tomo", MarkerKind.Volume, ["es"]),
        new("volumen", "Volumen", MarkerKind.Volume, ["es"]),
        new("cap[ií]tulo", "Capítulo", MarkerKind.Chapter, ["es", "pt-BR"]),
        new(@"cap\.", "Cap.", MarkerKind.Chapter, ["es", "pt-BR"]),

        new("band", "Band", MarkerKind.Volume, ["de"]),
        new(@"bd\.", "Bd.", MarkerKind.Volume, ["de"]),
        new("kapitel", "Kapitel", MarkerKind.Chapter, ["de"]),
        new(@"kap\.", "Kap.", MarkerKind.Chapter, ["de"]),

        new("tom", "Tom", MarkerKind.Volume, ["pl", "ru"]),
        new("rozdzia[łl]", "Rozdział", MarkerKind.Chapter, ["pl"]),
        new(@"rozdz\.", "Rozdz.", MarkerKind.Chapter, ["pl"]),

        new("том", "Том", MarkerKind.Volume, ["ru"]),
        new(@"т\.", "Т.", MarkerKind.Volume, ["ru"]),
        new("т", "Т", MarkerKind.WeakVolume, ["ru"]),
        new("глава", "Глава", MarkerKind.Chapter, ["ru"]),
        new(@"гл\.", "Гл.", MarkerKind.Chapter, ["ru"]),
        new("glava", "Glava", MarkerKind.Chapter, ["ru"]),

        // Spelled out because an invariant case fold does not pair the Turkish dotted and dotless i.
        new("c[iıİI]lt", "Cilt", MarkerKind.Volume, ["tr"]),
        new("b[oöÖ]l[uüÜ]m", "Bölüm", MarkerKind.Chapter, ["tr"]),

        new("巻", "巻", MarkerKind.VolumeSuffix, ["ja", "zh-Hans"]),
        new("冊", "冊", MarkerKind.VolumeSuffix, ["ja", "zh-Hans"]),
        new("卷", "卷", MarkerKind.VolumeSuffix, ["zh-Hans"]),
        new("册", "册", MarkerKind.VolumeSuffix, ["zh-Hans"]),
        new("권", "권", MarkerKind.VolumeSuffix, ["ko"]),
        new("話", "話", MarkerKind.ChapterSuffix, ["ja", "zh-Hans"]),
        new("回", "回", MarkerKind.ChapterSuffix, ["ja", "zh-Hans"]),
        new("章", "章", MarkerKind.ChapterSuffix, ["ja", "zh-Hans"]),
        new("话", "话", MarkerKind.ChapterSuffix, ["zh-Hans"]),
        new("화", "화", MarkerKind.ChapterSuffix, ["ko"]),
        new("장", "장", MarkerKind.ChapterSuffix, ["ko"]),
        new("편", "편", MarkerKind.ChapterSuffix, ["ko"])
    ];

    /// <summary>Every language with marker words of its own, in the order the language picker uses.</summary>
    public static IReadOnlyList<string> Languages { get; } = SupportedLanguages.All
        .Where(code => Words.Any(w => w.Languages.Contains(code)))
        .ToList();

    /// <summary>The shipped spelling of <paramref name="code"/>, or null when it has no marker words.</summary>
    public static string? Language(string? code) =>
        Languages.FirstOrDefault(l => l.Equals(code?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> VolumeWords(string language) =>
        Display(language, MarkerKind.Volume, MarkerKind.WeakVolume, MarkerKind.VolumeSuffix);

    public static IReadOnlyList<string> ChapterWords(string language) =>
        Display(language, MarkerKind.Chapter, MarkerKind.ChapterSuffix);

    private static IReadOnlyList<string> Display(string language, params MarkerKind[] kinds) =>
        Words.Where(w => kinds.Contains(w.Kind) && w.Languages.Contains(language)).Select(w => w.Display).ToList();
}
