using System.Text.Json;

namespace Mangarr.Core.Notifications;

/// <summary>Typed views over a connection's <c>ConfigJson</c>.</summary>
public static class NotificationConfig
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public record DiscordConfig(string? WebhookUrl);
    public record WebhookConfig(string? Url, string? BearerToken);

    public static DiscordConfig Discord(string json) =>
        Parse<DiscordConfig>(json) ?? new DiscordConfig(null);

    public static WebhookConfig Webhook(string json) =>
        Parse<WebhookConfig>(json) ?? new WebhookConfig(null, null);

    private static T? Parse<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
