namespace Maki.Sources.MangaPlus;

/// <summary>
/// MANGA Plus serves each language as its own catalog of its own title ids, and stamps every title
/// with a numeric language id (<c>Title.language</c>, protobuf field 7). English is 0 <em>or the
/// field absent entirely</em>, which is why the map has to distinguish "missing" from "zero".
/// <para>
/// The schema is unpublished, so the ids come from the client's own behaviour rather than a
/// definition file: the recorded <c>title_list/allV2</c> fixture carries a title under id 6 whose
/// name is in Thai script, and the rest follow the same order the app's language picker lists them
/// in. An id that is not here is skipped rather than guessed at — filing a Vietnamese title under
/// English would put chapters in the wrong series folder and mislabel the CBZ's
/// <c>LanguageISO</c>, which is worse than not offering it.
/// </para>
/// </summary>
internal static class MangaPlusLanguages
{
    private static readonly Dictionary<ulong, string> ById = new()
    {
        [0] = "en",
        [1] = "es",
        [2] = "fr",
        [3] = "id",
        [4] = "pt-br",
        [5] = "ru",
        [6] = "th",
        [7] = "de",
        [9] = "vi",
    };

    /// <summary>Human name for the picker, so two identically-titled entries are tellable apart.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["id"] = "Indonesian",
        ["pt-br"] = "Portuguese (Br)",
        ["ru"] = "Russian",
        ["th"] = "Thai",
        ["de"] = "German",
        ["vi"] = "Vietnamese",
    };

    public static readonly IReadOnlyList<string> Codes = ById.Values.Distinct().ToArray();

    /// <summary>The code for a title's language id, or null when the id is one we don't know.</summary>
    public static string? Code(ulong? id) =>
        id is null ? "en" : ById.GetValueOrDefault(id.Value);

    public static string Name(string? code) =>
        code is not null && Names.TryGetValue(code, out var name) ? name : code ?? "Unknown";
}
