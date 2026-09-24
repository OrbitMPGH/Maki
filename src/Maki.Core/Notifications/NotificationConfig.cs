using System.Text.Json;

namespace Maki.Core.Notifications;

/// <summary>Typed views over a connection's <c>ConfigJson</c>.</summary>
public sealed class NotificationFields(IReadOnlyDictionary<string, string> values)
{
    public string? this[string key] =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public string Require(string key) =>
        this[key] ?? throw new InvalidOperationException($"Notification field '{key}' is not configured");

    public bool Flag(string key) =>
        bool.TryParse(this[key], out var b) && b;

    public int? Int(string key) =>
        int.TryParse(this[key], out var i) ? i : null;
}

public static class NotificationConfig
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public record DiscordConfig(string? WebhookUrl);
    public record WebhookConfig(string? Url, string? BearerToken);

    public static DiscordConfig Discord(string json) =>
        Parse<DiscordConfig>(json) ?? new DiscordConfig(null);

    public static WebhookConfig Webhook(string json) =>
        Parse<WebhookConfig>(json) ?? new WebhookConfig(null, null);

    /// <summary>
    /// Flat string view of the JSON, keyed case-insensitively by field key. Non-string JSON values
    /// are kept as their raw text (so "5" and 5 both read as "5"). Missing keys read as null.
    /// </summary>
    public static NotificationFields Fields(string json)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new NotificationFields(dict);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new NotificationFields(dict);
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null => string.Empty,
                    _ => prop.Value.GetRawText()
                };
            }
        }
        catch (JsonException)
        {
        }

        return new NotificationFields(dict);
    }

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
