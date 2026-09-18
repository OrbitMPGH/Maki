using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Api.Services;

/// <summary>
/// The one reader of <see cref="SettingKeys.SourceLanguageOrder"/> and
/// <see cref="SettingKeys.SourceLanguagesDisabled"/>: which languages the instance wants downloaded,
/// most preferred first, and how that re-ranks the source priority list.
/// <para>
/// Each source lands in the bucket of the highest-ranked enabled language it publishes
/// (<see cref="ISource.SupportedLanguages"/>, compared with <see cref="LocalizedTitle.Matches"/> as
/// <c>SourceAvailability</c> does), buckets are concatenated in language order, and the base
/// <see cref="SettingKeys.SourcePriorityOrder"/> decides ties inside one. A source publishing none
/// of the enabled languages is left out of auto-matching entirely: searching it could only ever
/// produce a mapping whose chapters nobody asked for.
/// </para>
/// <para>
/// This is orthogonal to the per-source on/off switch. Enabling a language never flips a source on
/// or off, and <c>SourceAvailability</c> is untouched by anything here.
/// </para>
/// </summary>
/// <param name="Order">Codes in preferred order, switched-off ones included.</param>
/// <param name="Disabled">The subset of <paramref name="Order"/> that is switched off.</param>
public record SourceLanguagePreference(IReadOnlyList<string> Order, IReadOnlyList<string> Disabled)
{
    /// <summary>
    /// The wanted languages, most preferred first. Falls back to English alone when nothing is
    /// stored or everything is switched off, so a fresh install behaves exactly as it did before
    /// the setting existed.
    /// </summary>
    public IReadOnlyList<string> EnabledInOrder { get; } =
        Order.Where(code => !Disabled.Contains(code, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } enabled
            ? enabled
            : [SourceLanguages.Default];

    public static SourceLanguagePreference Parse(string? orderCsv, string? disabledCsv) =>
        new(SourceAvailability.Parse(orderCsv), SourceAvailability.Parse(disabledCsv));

    public static async Task<SourceLanguagePreference> LoadAsync(IAppSettings settings, CancellationToken ct = default) =>
        Parse(
            await settings.GetAsync(SettingKeys.SourceLanguageOrder, ct),
            await settings.GetAsync(SettingKeys.SourceLanguagesDisabled, ct));

    /// <summary>
    /// <paramref name="baseOrder"/> re-ranked by language, keeping only the sources that publish at
    /// least one enabled language. Stable on <c>(bucket, position in baseOrder)</c>.
    /// </summary>
    public static List<ISource> Rank(IReadOnlyList<ISource> baseOrder, SourceLanguagePreference pref) =>
        baseOrder
            .Select((source, position) => (source, position, bucket: BucketOf(source, pref)))
            .Where(item => item.bucket >= 0)
            .OrderBy(item => item.bucket)
            .ThenBy(item => item.position)
            .Select(item => item.source)
            .ToList();

    /// <summary>The sources <see cref="Rank"/> leaves out, in base order.</summary>
    public static List<ISource> Unranked(IReadOnlyList<ISource> baseOrder, SourceLanguagePreference pref) =>
        baseOrder.Where(source => BucketOf(source, pref) < 0).ToList();

    /// <summary>
    /// What a freshly auto-created mapping's <c>LanguageFilter</c> should hold: the enabled
    /// languages this source publishes, in preference order. Null for a source that does not honour
    /// a filter, and null when the answer is plain English, so an untouched-looking mapping keeps
    /// storing nothing (which <see cref="SourceLanguages.Parse"/> already reads as English).
    /// </summary>
    /// <remarks>
    /// MANGA Plus is not covered: a language there is a separate title id rather than a filter over
    /// one chapter list, so it declares no filter capability and gets null like any other source
    /// without one. A second language on it means a second mapping.
    /// </remarks>
    public static string? SeedFilter(ISource source, SourceLanguagePreference pref)
    {
        if (!source.Capabilities.HasFlag(SourceCapabilities.SupportsLanguageFilter))
        {
            return null;
        }

        var wanted = pref.EnabledInOrder.Where(code => Publishes(source, code)).ToList();
        return wanted.Count == 0 ? null : SourceLanguages.Serialize(wanted);
    }

    /// <summary>
    /// Every language any registered source publishes, deduped case-insensitively: English first,
    /// then the shipped UI languages in their own order, then anything only a source declares,
    /// alphabetically. The list the language picker offers, computed here so the frontend has no
    /// second opinion about it.
    /// </summary>
    public static IReadOnlyList<string> Offered(IReadOnlyCollection<ISource> all)
    {
        var seen = new List<string>();
        foreach (var code in all.SelectMany(s => s.SupportedLanguages))
        {
            if (!string.IsNullOrWhiteSpace(code) && !seen.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(code);
            }
        }

        int Rank(string code)
        {
            if (code.Equals(SourceLanguages.Default, StringComparison.OrdinalIgnoreCase)) return -1;
            var index = Array.FindIndex(
                Core.Localization.SupportedLanguages.All,
                c => c.Equals(code, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }

        return seen
            .OrderBy(Rank)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int BucketOf(ISource source, SourceLanguagePreference pref)
    {
        for (var i = 0; i < pref.EnabledInOrder.Count; i++)
        {
            if (Publishes(source, pref.EnabledInOrder[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Publishes(ISource source, string language) =>
        source.SupportedLanguages.Any(declared => LocalizedTitle.Matches(declared, language));
}
