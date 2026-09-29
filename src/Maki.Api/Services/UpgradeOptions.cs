using System.Globalization;
using Maki.Core.Configuration;

namespace Maki.Api.Services;

/// <summary>The instance-wide <c>upgrades.*</c> settings, read with their defaults applied.</summary>
public sealed record UpgradeOptions(
    bool Enabled,
    int? DefaultProfileId,
    int ScanHour,
    int MaxPerDay,
    int MaxProbesPerRun,
    int QuietPeriodDays,
    int TrashRetentionDays,
    bool ScanIncognito,
    bool VolumeSearch = true,
    long TorrentAutoGrabMaxBytes = UpgradeOptions.DefaultAutoGrabMaxBytes,
    int VolumeMissingTolerance = 3,
    int VolumeSearchesPerRun = 10,
    int ProposalExpiryDays = 30)
{
    public const long DefaultAutoGrabMaxBytes = 524288000;

    public static readonly UpgradeOptions Defaults = new(false, null, 4, 25, 50, 7, 14, true);

    /// <summary>The server's local time, which is what <c>scanHour</c> and <c>lastScanDate</c> are in.</summary>
    public static DateTime LocalNow(TimeProvider time) =>
        TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, TimeZoneInfo.Local);

    /// <summary>The <c>upgrades.lastScanDate</c> spelling of a local date.</summary>
    public static string MarkerDate(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static async Task<UpgradeOptions> LoadAsync(IAppSettings settings, CancellationToken ct)
    {
        async Task<int> Int(string key, int fallback, int min, int max) =>
            int.TryParse(await settings.GetAsync(key, ct), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, min, max)
                : fallback;

        var maxBytes = long.TryParse(await settings.GetAsync(SettingKeys.UpgradesTorrentAutoGrabMaxBytes, ct),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0
            ? bytes
            : Defaults.TorrentAutoGrabMaxBytes;

        return new UpgradeOptions(
            await settings.GetAsync(SettingKeys.UpgradesEnabled, ct) == "true",
            UpgradeEvaluationService.ParseId(await settings.GetAsync(SettingKeys.UpgradesDefaultProfileId, ct)),
            await Int(SettingKeys.UpgradesScanHour, Defaults.ScanHour, 0, 23),
            await Int(SettingKeys.UpgradesMaxPerDay, Defaults.MaxPerDay, 0, 1000),
            await Int(SettingKeys.UpgradesMaxProbesPerRun, Defaults.MaxProbesPerRun, 1, 500),
            await Int(SettingKeys.UpgradesQuietPeriodDays, Defaults.QuietPeriodDays, 0, 365),
            await Int(SettingKeys.UpgradesTrashRetentionDays, Defaults.TrashRetentionDays, 0, 365),
            await settings.GetAsync(SettingKeys.UpgradesScanIncognito, ct) != "false",
            await settings.GetAsync(SettingKeys.UpgradesVolumeSearch, ct) != "false",
            maxBytes,
            await Int(SettingKeys.UpgradesVolumeMissingTolerance, Defaults.VolumeMissingTolerance, 0, 50),
            await Int(SettingKeys.UpgradesVolumeSearchesPerRun, Defaults.VolumeSearchesPerRun, 1, 200),
            await Int(SettingKeys.UpgradesProposalExpiryDays, Defaults.ProposalExpiryDays, 1, 365));
    }
}
