using Maki.Core.Security;

namespace Maki.Data.Identity;

/// <summary>
/// One key/value setting belonging to one user, keyed <c>(UserId, Key)</c>.
/// <para>
/// Deliberately a separate table rather than widening <c>AppConfig</c>'s key with a user prefix:
/// that keyspace holds the instance's secrets (qBittorrent password, tracker client secrets, Kavita
/// API key) and mixing per-user rows into it would mean every "list the settings" query had to
/// filter them apart by string shape. It also keeps the cascade honest — deleting a user takes their
/// settings with them, which a flat prefix could not express.
/// </para>
/// <para>
/// Read through the scoped <c>IUserSettings</c>, never through <c>SettingsService</c>: that one is a
/// singleton that opens a fresh scope and DbContext per key, which is exactly the per-key round trip
/// <c>OpdsAccessService</c> exists to avoid.
/// </para>
/// </summary>
public class UserSetting : IUserOwned
{
    public int UserId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
