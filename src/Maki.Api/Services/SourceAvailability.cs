using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Api.Services;

/// <summary>
/// The one reader of <see cref="SettingKeys.SourcesDisabled"/> — a global off switch per source.
/// <para>
/// Turning a source off must not touch the per-series <c>SourceMapping.Enabled</c> flags: the
/// point of the switch is that flipping it back on restores exactly the per-series layout the
/// user had. So every place that asks "is this mapping live?" checks
/// <c>m.Enabled &amp;&amp; !disabled.Contains(m.SourceName)</c> rather than <c>m.Enabled</c> alone,
/// and nothing writes to the mappings when the setting changes.
/// </para>
/// <para>
/// A source with no explicit choice yet — never named in <see cref="SettingKeys.SourcePriorityOrder"/>,
/// which the priority page always writes in full — defaults on if it publishes English or the caller's
/// language, off otherwise. So a fresh non-English source (or a fresh install) doesn't quietly hand
/// every reader a wall of Turkish results; a reader whose UI is in Turkish gets it switched on for
/// them, everyone else can turn it on by hand. Once <c>SourcePriorityOrder</c> is saved with that
/// source named in it, the explicit <see cref="SettingKeys.SourcesDisabled"/> entry takes over and
/// this default never applies to it again.
/// </para>
/// <para>
/// This is a singleton reached from background jobs as well as requests (<c>ChapterSourceResolver</c>
/// is itself a singleton), so it can't take the scoped <c>IRequestLocale</c> directly. Callers that
/// have a viewer's own language — the two settings endpoints — pass it in; everything else falls back
/// to <see cref="SettingKeys.UiDefaultLanguage"/>, the same instance-wide fallback
/// <c>RequestLocaleContext</c> uses for contexts with no signed-in user.
/// </para>
/// </summary>
public class SourceAvailability(IAppSettings settings, SourceRegistry sourceRegistry)
{
    /// <summary>
    /// Names of globally-disabled sources, explicit choices plus the language default for anything
    /// not yet decided. A <see cref="List{T}"/> rather than a set because callers hand it straight to
    /// EF (<c>!disabled.Contains(m.SourceName)</c> becomes a SQL <c>NOT IN</c>); names are stored
    /// verbatim from the registry, so the compare is exact.
    /// </summary>
    public async Task<List<string>> DisabledAsync(CancellationToken ct = default, string? language = null)
    {
        var explicitlyDisabled = Parse(await settings.GetAsync(SettingKeys.SourcesDisabled, ct));
        var decided = Parse(await settings.GetAsync(SettingKeys.SourcePriorityOrder, ct));
        var effectiveLanguage = Core.Localization.SupportedLanguages.Resolve(
            language ?? await settings.GetAsync(SettingKeys.UiDefaultLanguage, ct));

        foreach (var source in sourceRegistry.All)
        {
            if (decided.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue; // Already has an explicit on/off choice — the setting above is authoritative.
            }

            // English always defaults on (today's behaviour for the thirteen English-only
            // sources); a non-English source also defaults on when it matches the caller's
            // language, off for everyone else.
            var defaultsOn = source.SupportedLanguages.Any(lang =>
                LocalizedTitle.Matches(lang, "en") || LocalizedTitle.Matches(lang, effectiveLanguage));
            if (!defaultsOn)
            {
                explicitlyDisabled.Add(source.Name);
            }
        }

        return explicitlyDisabled;
    }

    public async Task<bool> IsEnabledAsync(string sourceName, CancellationToken ct = default, string? language = null) =>
        !(await DisabledAsync(ct, language)).Contains(sourceName, StringComparer.OrdinalIgnoreCase);

    public static List<string> Parse(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
