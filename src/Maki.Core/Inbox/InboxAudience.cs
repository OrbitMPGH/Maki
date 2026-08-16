namespace Maki.Core.Inbox;

/// <summary>Which of the three audience rules an <see cref="InboxAudience"/> names.</summary>
public enum InboxAudienceKind
{
    /// <summary>One person, because the event happened to them.</summary>
    User,

    /// <summary>Everyone who administers the instance.</summary>
    Admins,

    /// <summary>Whoever is reading, has read, or asked for a particular series.</summary>
    SeriesTrackers,
}

/// <summary>
/// Who should receive a notification. A closed set of three rules rather than a list of user ids,
/// so the rule is decided at the raise site (where the meaning of the event is obvious) and the
/// membership is resolved once, in one place.
/// </summary>
public readonly record struct InboxAudience
{
    private InboxAudience(InboxAudienceKind kind, int userId, int seriesId, int rootFolderId)
    {
        Kind = kind;
        UserId = userId;
        SeriesId = seriesId;
        RootFolderId = rootFolderId;
    }

    public InboxAudienceKind Kind { get; }

    /// <summary>Set for <see cref="InboxAudienceKind.User"/>.</summary>
    public int UserId { get; }

    /// <summary>Set for <see cref="InboxAudienceKind.SeriesTrackers"/>.</summary>
    public int SeriesId { get; }

    /// <summary>Set for <see cref="InboxAudienceKind.SeriesTrackers"/>.</summary>
    public int RootFolderId { get; }

    public static InboxAudience User(int userId) =>
        new(InboxAudienceKind.User, userId, 0, 0);

    public static InboxAudience Admins { get; } =
        new(InboxAudienceKind.Admins, 0, 0, 0);

    /// <summary>
    /// The users who track <paramref name="seriesId"/>, narrowed to those who can see its root
    /// folder.
    /// </summary>
    /// <param name="rootFolderId">
    /// Passed in rather than looked up, for the same reason <c>EventBroadcaster.ChapterImported</c>
    /// takes it: the callers are background workers whose <c>DataScope</c> is unrestricted only by
    /// convention, and a lookup through the <c>Series</c> query filter would depend on that holding.
    /// </param>
    public static InboxAudience SeriesTrackers(int seriesId, int rootFolderId) =>
        new(InboxAudienceKind.SeriesTrackers, 0, seriesId, rootFolderId);
}
