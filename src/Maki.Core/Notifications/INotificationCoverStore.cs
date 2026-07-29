namespace Maki.Core.Notifications;

/// <summary>
/// Resolves the locally stored poster for a series so a provider can attach it to a message.
/// This is a file path rather than a URL on purpose: a self-hosted Maki is usually not reachable
/// from Discord's CDN, so the image has to be uploaded with the message, not linked to.
/// </summary>
public interface INotificationCoverStore
{
    /// <summary>Absolute path to the stored poster, or null when the series has none on disk.</summary>
    string? PosterPathFor(int seriesId);
}
