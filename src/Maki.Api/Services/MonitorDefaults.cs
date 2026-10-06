using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Services;

/// <summary>
/// The mode a series starts with when whoever added it did not pick one: the add form with
/// monitoring on, a library import, an approved request with no mode. Import lists keep their own
/// per-tracker choice.
/// </summary>
public static class MonitorDefaults
{
    /// <summary>None is not offered: "monitor new chapters" switched on and then defaulting to none would contradict itself.</summary>
    public static readonly NewChapterMonitorMode[] Choosable =
        [NewChapterMonitorMode.All, NewChapterMonitorMode.MainOnly, NewChapterMonitorMode.Smart];

    public static NewChapterMonitorMode Parse(string? stored) =>
        Enum.TryParse<NewChapterMonitorMode>(stored, true, out var mode) && Choosable.Contains(mode)
            ? mode
            : NewChapterMonitorMode.All;

    /// <summary>
    /// The setting, with All narrowed to MainOnly when specials are skipped, the same downgrade an
    /// explicit All gets in <c>SeriesCreationService</c>. Smart reads that setting on its own.
    /// </summary>
    public static async Task<NewChapterMonitorMode> ForNewSeriesAsync(IAppSettings settings, CancellationToken ct)
    {
        var mode = Parse(await settings.GetAsync(SettingKeys.MonitoringDefaultMode, ct));
        return mode == NewChapterMonitorMode.All &&
               await settings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"
            ? NewChapterMonitorMode.MainOnly
            : mode;
    }
}
