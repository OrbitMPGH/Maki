namespace Maki.Core.Entities;

public enum NotificationType
{
    Discord = 0,
    Webhook = 1
}

/// <summary>
/// A user-defined outbound notification connection (Sonarr/Radarr-style "Connect").
/// One row per connection; <see cref="ConfigJson"/> holds provider-specific fields
/// (webhook URLs, tokens) as JSON. Those are secrets stored in plaintext — same
/// boundary as the rest of the config (see CLAUDE.md): the config dir and any DB copy
/// are credential material.
/// </summary>
public class Notification
{
    public int Id { get; set; }

    /// <summary>User-facing label.</summary>
    public string Name { get; set; } = string.Empty;

    public NotificationType Type { get; set; }

    /// <summary>Provider-specific config, JSON. Discord: {"webhookUrl"}. Webhook: {"url","bearerToken"?}.</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    // Per-event toggles.
    public bool OnChapterDownloaded { get; set; }
    public bool OnDownloadFailed { get; set; }
    public bool OnNewChapterAvailable { get; set; }
    public bool OnImportCompleted { get; set; }
    public bool OnHealthIssue { get; set; }
}
