namespace Maki.Core.Notifications;

/// <summary>
/// Loads the series poster bytes for providers that upload the image with the message.
/// Failures are swallowed: an unreadable cover must cost the message its image, never its delivery.
/// </summary>
public static class NotificationPoster
{
    public const string FileName = "poster.jpg";
    public const string ContentType = "image/jpeg";

    public static byte[]? Read(INotificationCoverStore? covers, NotificationMessage message)
    {
        if (covers is null || message.SeriesId is not { } seriesId)
        {
            return null;
        }

        try
        {
            var path = covers.PosterPathFor(seriesId);
            return path is null ? null : File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
