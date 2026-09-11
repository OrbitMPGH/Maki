using Maki.Core.Configuration;

namespace Maki.Api.Services;

/// <summary>
/// Resolving a user's time zone from their stored setting.
///
/// <para>
/// A free function rather than a method on <c>UserMetricsService</c> because that service is scoped
/// (it holds a <c>MakiDbContext</c>) and the singletons that need day boundaries cannot take it
/// without capturing a context for the life of the process.
/// </para>
/// </summary>
public static class UserTimeZone
{
    /// <summary>
    /// The user's time zone, or UTC. A bad or unknown id resolves to UTC rather than throwing: the
    /// value arrives from a browser and the set of ids a host recognises is not guaranteed, and a
    /// stats page that 500s because somebody's zone was renamed upstream is a worse failure than a
    /// day boundary in the wrong place.
    /// </summary>
    public static async Task<TimeZoneInfo> ResolveAsync(
        IUserSettingsStore userSettings, int userId, CancellationToken ct = default)
    {
        var id = await userSettings.GetAsync(userId, SettingKeys.UserTimeZone, ct);
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
