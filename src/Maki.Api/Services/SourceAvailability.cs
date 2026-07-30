using Maki.Core.Configuration;

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
/// </summary>
public class SourceAvailability(IAppSettings settings)
{
    /// <summary>
    /// Names of globally-disabled sources. A <see cref="List{T}"/> rather than a set because
    /// callers hand it straight to EF (<c>!disabled.Contains(m.SourceName)</c> becomes a SQL
    /// <c>NOT IN</c>); names are stored verbatim from the registry, so the compare is exact.
    /// </summary>
    public async Task<List<string>> DisabledAsync(CancellationToken ct = default) =>
        Parse(await settings.GetAsync(SettingKeys.SourcesDisabled, ct));

    public async Task<bool> IsEnabledAsync(string sourceName, CancellationToken ct = default) =>
        !(await DisabledAsync(ct)).Contains(sourceName, StringComparer.OrdinalIgnoreCase);

    public static List<string> Parse(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
