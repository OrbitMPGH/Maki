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
/// which the priority page always writes in full — defaults on if it publishes English or
/// <see cref="SettingKeys.UiDefaultLanguage"/>, off otherwise. So a fresh non-English source (or a
/// fresh install) doesn't quietly hand every reader a wall of Turkish results, while an instance
/// that is itself Turkish gets it switched on. Once <c>SourcePriorityOrder</c> is saved with that
/// source named in it, the explicit <see cref="SettingKeys.SourcesDisabled"/> entry takes over and
/// this default never applies to it again.
/// </para>
/// <para>
/// The answer is deliberately the same for every caller, and never keyed off the viewer's own
/// locale. This gates shared state — which mappings run, which chapters download, which sources a
/// background job may reach — so a per-viewer answer would disagree with itself: a reader whose UI
/// matched a non-English source used to be shown it as enabled by <c>SearchController.ListSources</c>
/// and then refused when they tried to link it, because <c>SourceMappingController.Create</c> and
/// every job resolve the default instance-wide. This is also a singleton reached from background
/// jobs as well as requests (<c>ChapterSourceResolver</c> is itself a singleton), so the scoped
/// <c>IRequestLocale</c> isn't available to it anyway.
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
    public async Task<List<string>> DisabledAsync(CancellationToken ct = default)
    {
        var explicitlyDisabled = Parse(await settings.GetAsync(SettingKeys.SourcesDisabled, ct));
        var decided = Parse(await settings.GetAsync(SettingKeys.SourcePriorityOrder, ct));
        var instanceLanguage = Core.Localization.SupportedLanguages.Resolve(
            await settings.GetAsync(SettingKeys.UiDefaultLanguage, ct));

        foreach (var source in sourceRegistry.All)
        {
            if (decided.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue; // Already has an explicit on/off choice — the setting above is authoritative.
            }

            if (!DefaultsOn(source, instanceLanguage))
            {
                explicitlyDisabled.Add(source.Name);
            }
        }

        return explicitlyDisabled;
    }

    /// <summary>
    /// Whether <paramref name="source"/> would be enabled by Maki's default rule when it has no
    /// explicit choice yet. See the class remarks. Shared by <see cref="DisabledAsync"/> and
    /// <c>SearchController.ListSources</c>, which reports it per source without disabling anything.
    /// </summary>
    public static bool DefaultsOn(ISource source, string instanceLanguage) =>
        // English always defaults on (today's behaviour for the thirteen English-only sources);
        // a non-English source also defaults on when it matches the instance's own language.
        source.SupportedLanguages.Any(lang =>
            LocalizedTitle.Matches(lang, "en") || LocalizedTitle.Matches(lang, instanceLanguage));

    public async Task<bool> IsEnabledAsync(string sourceName, CancellationToken ct = default) =>
        !(await DisabledAsync(ct)).Contains(sourceName, StringComparer.OrdinalIgnoreCase);

    public static List<string> Parse(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
