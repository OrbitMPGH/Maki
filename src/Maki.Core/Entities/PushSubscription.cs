using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// One browser's Web Push subscription. A user has one per device they enabled push on.
/// <para>
/// <see cref="Endpoint"/> is unique across users: it names a browser profile, not an account, so a
/// device that signs in as someone else re-registers the same endpoint and the row moves to the new
/// owner rather than notifying the previous one.
/// </para>
/// </summary>
public class PushSubscription : IUserOwned
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>The push service URL the browser handed out. Treated as a secret: anyone holding it and the keys can push to the device.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The browser's P-256 public key, base64url, used to encrypt each payload.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The browser's auth secret, base64url.</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>For the settings card, so a user can tell their devices apart.</summary>
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Consecutive failed sends. Reset by a success; the row is deleted when it reaches the limit.</summary>
    public int FailureCount { get; set; }
}
