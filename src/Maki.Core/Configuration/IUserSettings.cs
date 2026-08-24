namespace Maki.Core.Configuration;

/// <summary>
/// The key/value settings of <em>one</em> user, over the <c>UserSettings</c> table. Scoped, bound to
/// the current request's user — unlike <see cref="IAppSettings"/>, which is a singleton over the
/// instance-wide <c>AppConfig</c>.
/// <para>
/// <see cref="GetManyAsync"/> exists because the singleton's shape does not survive contact with a
/// page that needs several keys: it opens a fresh scope and DbContext per key, which is three round
/// trips for a reader manifest and one per tracker for the scrobble settings page. Reach for the bulk
/// read whenever more than one key is wanted at once.
/// </para>
/// </summary>
public interface IUserSettings
{
    /// <summary>Whose settings these are. 0 when nobody is authenticated — reads then find nothing.</summary>
    int UserId { get; }

    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// One query for many keys. Missing keys are simply absent from the result, so callers apply their
    /// own defaults exactly as they do with <see cref="GetAsync"/> returning null.
    /// </summary>
    Task<Dictionary<string, string>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>A null or blank value deletes the row, so "unset" and "default" stay the same state.</summary>
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

/// <summary>
/// Per-user settings for an <em>arbitrary</em> user, for code that runs outside a request and so has
/// no "current user": the scrobble tick walking every connected account, and the trackers themselves,
/// which need one user's Kitsu credentials or MangaBaka token while pushing that user's reading.
/// <para>
/// Deliberately separate from <see cref="IUserSettings"/> rather than an extra overload on it. That
/// one is scoped and answers "my settings", which is what a controller wants and is impossible to
/// misuse; this one can read anybody's, so every call has to name whose.
/// </para>
/// </summary>
public interface IUserSettingsStore
{
    Task<string?> GetAsync(int userId, string key, CancellationToken ct = default);
    Task SetAsync(int userId, string key, string? value, CancellationToken ct = default);
}

/// <summary>
/// The <see cref="SettingKeys"/> entries that live per user rather than per instance, listed in one
/// place because two things need the whole set: the migration that moved them out of <c>AppConfig</c>
/// into the first user's rows, and the settings endpoints that must not accidentally serve one
/// person's preference to another.
/// <para>
/// The dividing line: an instance setting describes the deployment (ports, paths, connections to
/// Prowlarr/qBittorrent/Kavita, source priority, update checks) or an app registration shared by
/// everyone (a tracker's client id and secret). A user setting describes a person — what their reader
/// looks like, which page they land on, which trackers they push to, and the OAuth token their
/// registration was exchanged for.
/// </para>
/// </summary>
public static class UserSettingKeys
{
    /// <summary>
    /// Keys whose value is a plain per-user setting, moved wholesale from <c>AppConfig</c>. Does not
    /// include the per-service toggles, which are generated names — see <see cref="IsPerUser"/>.
    /// </summary>
    public static readonly string[] Fixed =
    [
        SettingKeys.ReaderPrefs,
        SettingKeys.ReaderPushToKavita,
        SettingKeys.UiStartPage,
        SettingKeys.UiHomeSections,
        SettingKeys.UiSeriesSections,
        SettingKeys.RecommendationsDefaults,
        SettingKeys.DiscoverSearchDefaults,
        SettingKeys.OpdsEnabled,
        SettingKeys.OpdsTrackProgress,
        SettingKeys.ScrobblePlanToRead,
        SettingKeys.ScrobbleMangaBakaToken,
        SettingKeys.ScrobbleKitsuEmail,
        SettingKeys.ScrobbleKitsuPassword,
        SettingKeys.UserTimeZone,
        SettingKeys.UserGamification,
        SettingKeys.NotificationsInbox,
        SettingKeys.ProgressLastNotifiedLevel,
    ];

    /// <summary>
    /// Whether a key belongs to a user. Covers <see cref="Fixed"/> plus the generated
    /// <c>scrobble.{service}.reading</c> / <c>.ratings</c> toggles, which are per-service and so
    /// cannot be enumerated without knowing the registered trackers.
    /// </summary>
    public static bool IsPerUser(string key) =>
        Fixed.Contains(key) ||
        (key.StartsWith("scrobble.", StringComparison.Ordinal) &&
         (key.EndsWith(".reading", StringComparison.Ordinal) || key.EndsWith(".ratings", StringComparison.Ordinal)));
}
