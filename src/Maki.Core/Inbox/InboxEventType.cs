namespace Maki.Core.Inbox;

/// <summary>
/// What an in-app notification is about. Deliberately a separate enum from
/// <c>NotificationEventType</c>, which belongs to the outbound Discord/webhook pipeline: that one is
/// instance-wide and its subscribers are chat channels, so pushing achievements and level-ups through
/// it would bury the download reports it exists for.
/// <para>
/// Values persist in <c>UserNotifications.Type</c> and double as the keys of a user's per-event
/// preference spec. Append only — never renumber and never reuse a retired value, same discipline as
/// <c>MakiPermission</c> and <c>UserAchievement.Key</c>, or somebody's stored preferences start
/// pointing at a different event.
/// </para>
/// </summary>
public enum InboxEventType
{
    /// <summary>Never raised. A row that reads as this came from a build that knew a value this one
    /// does not, and is rendered as a plain message rather than mapped to the wrong icon.</summary>
    Unknown = 0,

    NewChapterAvailable = 1,
    SmartDownloadQueued = 2,
    ChapterDownloaded = 3,
    DownloadFailed = 4,
    AchievementUnlocked = 5,
    LevelUp = 6,
    RequestSubmitted = 7,
    RequestApproved = 8,
    RequestRejected = 9,
    RequestEdited = 10,
    HealthIssue = 11,
    UpdateAvailable = 12,
    ImportFinished = 13,
    BackupFinished = 14,
    SourceMatchFinished = 15,
}

/// <summary>
/// Which events only ever go to admins. Used twice: the resolver refuses to widen them, and the
/// settings UI hides their toggles from users who could never receive them anyway.
/// </summary>
public static class InboxEventTypes
{
    public static readonly InboxEventType[] All = Enum.GetValues<InboxEventType>()
        .Where(t => t != InboxEventType.Unknown)
        .ToArray();

    public static bool IsAdminOnly(InboxEventType type) => type is
        InboxEventType.HealthIssue or
        InboxEventType.UpdateAvailable or
        InboxEventType.ImportFinished or
        InboxEventType.BackupFinished or
        InboxEventType.RequestSubmitted;

    /// <summary>
    /// Events that are off unless the user turns them on. Only one so far: a finished source match
    /// already redraws the series' Sources card live, so an inbox row for it is a second copy of
    /// something the user is looking at.
    /// </summary>
    public static bool DefaultsOff(InboxEventType type) => type is InboxEventType.SourceMatchFinished;

    /// <summary>The camelCase name a preference spec and the API use for a type.</summary>
    public static string Key(InboxEventType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
