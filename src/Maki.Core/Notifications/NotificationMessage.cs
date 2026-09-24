namespace Maki.Core.Notifications;

public enum NotificationEventType
{
    Test,
    ChapterDownloaded,
    DownloadFailed,
    NewChapterAvailable,
    ImportCompleted,
    HealthIssue,
    UpdateAvailable,
    SeriesAdded,
    SeriesRemoved,
    RequestSubmitted,
    RequestResolved,
    ManualMatchNeeded
}

public enum NotificationLevel
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One notification to deliver, provider-agnostic. Providers shape it into their own format.
/// <c>SeriesLabel</c>/<c>ChapterLabel</c> are filled by <c>NotificationService</c> in the instance
/// language before a provider sees the message.
/// <c>SeriesTagIds</c> stands in for the tag lookup on <c>SeriesId</c> when the series is already
/// gone (a removal), so tag-scoped connections still see it.
/// </summary>
public record NotificationMessage(
    NotificationEventType EventType,
    string Title,
    string Body,
    NotificationLevel Level = NotificationLevel.Info,
    string? SeriesTitle = null,
    int? SeriesId = null,
    string? ChapterNumber = null,
    string? Url = null,
    string SeriesLabel = "Series",
    string ChapterLabel = "Chapter",
    IReadOnlyCollection<int>? SeriesTagIds = null);
