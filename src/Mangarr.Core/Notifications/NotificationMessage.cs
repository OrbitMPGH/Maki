namespace Mangarr.Core.Notifications;

public enum NotificationEventType
{
    Test,
    ChapterDownloaded,
    DownloadFailed,
    NewChapterAvailable,
    ImportCompleted,
    HealthIssue
}

public enum NotificationLevel
{
    Info,
    Warning,
    Error
}

/// <summary>One notification to deliver, provider-agnostic. Providers shape it into their own format.</summary>
public record NotificationMessage(
    NotificationEventType EventType,
    string Title,
    string Body,
    NotificationLevel Level = NotificationLevel.Info,
    string? SeriesTitle = null,
    int? SeriesId = null,
    string? ChapterNumber = null,
    string? Url = null);
