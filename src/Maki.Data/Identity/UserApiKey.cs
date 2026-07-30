namespace Maki.Data.Identity;

/// <summary>What a key may be used for.</summary>
public enum UserApiKeyScope
{
    /// <summary>The whole API, as that user, via the <c>X-Api-Key</c> header.</summary>
    Full = 0,

    /// <summary>
    /// The OPDS catalogue only, as the token embedded in the feed URL. Separate from
    /// <see cref="Full"/> because that URL is pasted into third-party reading apps: a full-scope
    /// key there would hand them the entire management API.
    /// </summary>
    Opds = 1,
}

/// <summary>
/// A long-lived credential belonging to one user — either an API key for scripts and third-party
/// clients, or the token in an OPDS feed URL. Replaces both the single instance-wide API key that
/// used to live in <c>config.json</c> and the single instance-wide <c>opds.token</c> setting.
/// <para>
/// Only the SHA-256 digest is stored, so the key cannot be recovered from the database and lookup
/// is a plain indexed match on a digest rather than a comparison against a secret — there is no
/// timing side channel to close, unlike the string equality the old API key middleware used.
/// The plaintext is shown to the user exactly once, at creation.
/// </para>
/// </summary>
public class UserApiKey
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>User-supplied label, so a key can be identified before revoking it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the key. Unique — the lookup index.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First few characters of the plaintext, for display. Too short to be brute-forced into the whole key.</summary>
    public string Prefix { get; set; } = string.Empty;

    public UserApiKeyScope Scope { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Best-effort, and deliberately not written on every request: an OPDS reader prefetching pages
    /// would turn one chapter into hundreds of writes. Updated at most once per interval.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Set instead of deleting the row, so a revoked key stays visible in the account UI and in the
    /// audit trail. A revoked key authenticates nothing.
    /// </summary>
    public DateTime? RevokedAt { get; set; }
}
