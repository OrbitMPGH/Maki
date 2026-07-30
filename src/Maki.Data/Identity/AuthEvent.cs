namespace Maki.Data.Identity;

public enum AuthEventType
{
    LoginSucceeded = 0,
    LoginFailed = 1,
    LockedOut = 2,
    LoggedOut = 3,
    SetupCompleted = 4,
    PasswordChanged = 5,
    TwoFactorEnabled = 6,
    TwoFactorDisabled = 7,
    ApiKeyCreated = 8,
    ApiKeyRevoked = 9,
    UserCreated = 10,
    UserUpdated = 11,
    UserDeleted = 12,
    PermissionsChanged = 13,
    SessionsRevoked = 14,
    OidcLinked = 15,
    OidcProvisioned = 16,
}

/// <summary>
/// Security audit trail. An instance reachable from the internet needs a record of who signed in,
/// what failed, and who changed whose permissions — none of which is reconstructable from the
/// Serilog request log, which deliberately never records request bodies.
/// <para>
/// Capped to the most recent rows on write, the same way <c>ScrobbleLogEntry</c> is: this table is
/// append-only on a hot path (every failed login attempt against an exposed instance lands here)
/// and must not be allowed to grow without bound.
/// </para>
/// </summary>
public class AuthEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>Null when the event has no account — a failed login for a username that does not exist.</summary>
    public int? UserId { get; set; }

    /// <summary>
    /// Denormalized so the row survives the account being deleted, and so a failed login records
    /// the username that was <em>attempted</em> rather than nothing at all.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    public AuthEventType Type { get; set; }

    /// <summary>
    /// Remote address as the pipeline saw it. Only trustworthy behind a proxy when
    /// <c>auth.trustedproxies</c> is configured — without it, forwarded headers are ignored
    /// precisely so a forged one cannot poison this column or dodge rate limiting.
    /// </summary>
    public string? ClientIp { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>
    /// Free text for the case worth debugging — which permission changed, which key was revoked,
    /// why a login failed. Never a credential.
    /// </summary>
    public string? Detail { get; set; }
}
