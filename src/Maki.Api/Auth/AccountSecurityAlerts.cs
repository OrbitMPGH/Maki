using System.Globalization;
using Maki.Api.Services;
using Maki.Core.Inbox;
using Maki.Core.Notifications;

namespace Maki.Api.Auth;

/// <summary>
/// Tells an account's owner when something that guards the account changed. A hijacked session or a
/// rogue admin edit is otherwise invisible to the person it happened to. Every message states what
/// changed and when, and none carries a key, token or password.
/// </summary>
public class AccountSecurityAlerts(InboxService inbox, TimeProvider clock)
{
    public const string PasswordChanged = "inbox.account.passwordChanged";
    public const string PasswordReset = "inbox.account.passwordReset";
    public const string TwoFactorEnabled = "inbox.account.twoFactorEnabled";
    public const string TwoFactorDisabled = "inbox.account.twoFactorDisabled";
    public const string TwoFactorReset = "inbox.account.twoFactorReset";
    public const string ApiKeyCreated = "inbox.account.apiKeyCreated";
    public const string OpdsTokenCreated = "inbox.account.opdsTokenCreated";
    public const string OpdsTokenRotated = "inbox.account.opdsTokenRotated";
    public const string PermissionsChanged = "inbox.account.permissionsChanged";
    public const string FolderAccessChanged = "inbox.account.folderAccessChanged";

    public Task RaiseAsync(int userId, string key, string? name = null, CancellationToken ct = default)
    {
        var when = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var args = name is null
            ? InboxMessage.Args(new { when })
            : InboxMessage.Args(new { when, name });
        return inbox.RaiseAsync(
            InboxEventType.AccountSecurity,
            new InboxMessage(key, args, NotificationLevel.Warning, Url: "/settings"),
            InboxAudience.User(userId),
            ct);
    }
}
