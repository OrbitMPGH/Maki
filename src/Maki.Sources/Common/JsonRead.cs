using System.Globalization;
using System.Text.Json;

namespace Maki.Sources.Common;

/// <summary>
/// Readers for fields an upstream API may send as either a JSON string or a JSON number (an id,
/// a chapter number, a page order). A strict <c>GetString</c>/<c>GetInt32</c> turns a harmless
/// type change into an exception that fails the whole search, list or chapter.
/// </summary>
internal static class JsonRead
{
    /// <summary>The string, or the number's literal text; null for anything else.</summary>
    public static string? Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null
    };

    /// <summary>An integer from a number (whole or "3.0") or a numeric string; null when it is neither.</summary>
    public static int? Int(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var whole))
                {
                    return whole;
                }

                return element.TryGetDouble(out var real) && real is >= int.MinValue and <= int.MaxValue
                    ? (int)Math.Round(real)
                    : null;
            case JsonValueKind.String:
                return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>A property of an object as <see cref="Text"/>; null when the element is not an object or lacks it.</summary>
    public static string? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? Text(value) : null;

    /// <summary>Fetches a JSON document through the client and returns a root that outlives the parse.</summary>
    public static async Task<JsonElement> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        var body = await client.GetStringAsync(path, ct);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
