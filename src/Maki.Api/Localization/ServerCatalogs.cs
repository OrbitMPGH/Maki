using System.Collections.Concurrent;
using System.Reflection;
using Karambolo.PO;
using Maki.Core.Localization;

namespace Maki.Api.Localization;

/// <summary>
/// The parsed <c>server.po</c> catalogues, one per shipped language, read from the assembly's
/// embedded resources and cached for the life of the process.
/// <para>
/// Embedded rather than loose on disk so <c>dotnet publish</c> needs no path juggling and an
/// operator has no half-editable copy sitting beside the binary. The resource name is
/// <c>Maki.Locales.{locale}.po</c>; see the <c>EmbeddedResource</c> item in <c>Maki.Api.csproj</c>,
/// which lifts the locale out of the folder name.
/// </para>
/// <para>
/// Singleton: catalogues are immutable once parsed, a few hundred entries each, and parsing one per
/// request would be pure waste.
/// </para>
/// </summary>
public sealed class ServerCatalogs
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ServerCatalogs> _logger;

    public ServerCatalogs(ILogger<ServerCatalogs> logger) => _logger = logger;

    /// <summary>
    /// The translation for <paramref name="key"/>, or null when this language does not have one.
    /// <para>
    /// An entry present but empty counts as absent. That is what an untranslated row looks like in a
    /// PO file, and it is the state every key starts in for the twelve languages nobody has reviewed
    /// yet, so it has to read as "fall back" rather than as an empty string on screen.
    /// </para>
    /// </summary>
    public string? Lookup(string locale, string key)
    {
        var catalog = Load(locale);
        return catalog.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    private IReadOnlyDictionary<string, string> Load(string locale) =>
        _cache.GetOrAdd(locale, ParseEmbedded);

    private IReadOnlyDictionary<string, string> ParseEmbedded(string locale)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = $"Maki.Locales.{locale}.po";

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            // Not fatal: Lookup answers null and the caller falls back to English. A language on
            // SupportedLanguages with no catalogue embedded is a build mistake rather than a runtime
            // one, so say so once and carry on serving English.
            _logger.LogWarning("No embedded message catalogue {Resource}; falling back to English", resource);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new POParser().Parse(stream);
        if (!result.Success)
        {
            _logger.LogError(
                "Message catalogue {Resource} failed to parse; falling back to English. {Diagnostics}",
                resource, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in result.Catalog)
        {
            // Singular entries only. Plurals are expressed as ICU inside one message here, the same
            // way the client catalogue does it, rather than as gettext plural forms: that keeps one
            // syntax across both catalogues and lets MessageFormat apply the CLDR rules.
            if (entry.Count > 0) map[entry.Key.Id] = entry[0];
        }
        return map;
    }

    /// <summary>
    /// Every key this language defines. Only used by the catalogue guard test, which is also the
    /// reason it exists: without it, a key renamed in C# and not in the PO file is invisible until
    /// somebody hits that error path in that language.
    /// </summary>
    internal IReadOnlyCollection<string> Keys(string locale) => Load(locale).Keys.ToArray();

    /// <summary>Whether the language has a catalogue embedded at all.</summary>
    internal bool Exists(string locale) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream($"Maki.Locales.{locale}.po") is not null;

    internal static IReadOnlyList<string> Shipped => SupportedLanguages.All;
}
