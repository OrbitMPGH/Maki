using Maki.Core.Configuration;

namespace Maki.Api.Services;

/// <summary>
/// Remembers which version the instance ran before its latest upgrade, so the Settings page can
/// list the settings added since. Runs once at startup, after migrations.
/// </summary>
public static class VersionHistory
{
    /// <summary>Stands in for "some version older than this feature" on an existing install.</summary>
    public const string BeforeTracking = "0.0.0";

    public static async Task RecordAsync(IAppSettings settings, string current, bool freshDatabase, CancellationToken ct = default)
    {
        var running = await settings.GetAsync(SettingKeys.VersionRunning, ct);
        if (string.Equals(running, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // No record on a database that already had data means an install from before this key
        // existed: everything tagged is new to it. A fresh database has nothing to compare against.
        var upgradedFrom = running ?? (freshDatabase ? null : BeforeTracking);
        await settings.SetAsync(SettingKeys.VersionUpgradedFrom, upgradedFrom, ct);
        await settings.SetAsync(SettingKeys.VersionRunning, current, ct);
    }
}
