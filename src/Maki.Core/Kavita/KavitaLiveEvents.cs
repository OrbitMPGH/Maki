using System.Text;
using System.Text.Json;

namespace Maki.Core.Kavita;

/// <summary>
/// The two things Maki reads out of Kavita's realtime channel: who the API key belongs to, and which
/// series a <c>UserProgressUpdate</c> event is about.
/// <para>
/// Kavita sends <c>UserProgressUpdate</c> to online <em>admins</em> only, for every user's reading.
/// So the API key has to belong to an admin for anything to arrive, and every event has to be
/// filtered down to the key's own user, or another household member's reading would be marked as
/// the bound Maki user's.
/// </para>
/// </summary>
public static class KavitaLiveEvents
{
    public const string ProgressEvent = "UserProgressUpdate";

    public readonly record struct TokenIdentity(int UserId, bool IsAdmin);

    /// <summary>
    /// Reads the user id and roles from a Kavita JWT without validating it. Maki only uses this to
    /// decide whether to listen and which events are its own; Kavita validates the token itself.
    /// </summary>
    public static TokenIdentity? ReadToken(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = doc.RootElement;
            if (!root.TryGetProperty("nameid", out var idProp) ||
                !int.TryParse(idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.GetRawText(),
                    out var userId))
            {
                return null;
            }

            var isAdmin = root.TryGetProperty("role", out var roles) && roles.ValueKind switch
            {
                JsonValueKind.String => IsAdminRole(roles.GetString()),
                JsonValueKind.Array => roles.EnumerateArray().Any(r => IsAdminRole(r.GetString())),
                _ => false,
            };

            return new TokenIdentity(userId, isAdmin);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Kavita series id of a progress event, or null when the event is about a different Kavita
    /// user or doesn't carry one.
    /// </summary>
    public static int? SeriesIdFor(JsonElement message, int kavitaUserId)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !TryGet(message, "body", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGet(body, "userId", out var user) || !user.TryGetInt32(out var userId) || userId != kavitaUserId)
        {
            return null;
        }

        return TryGet(body, "seriesId", out var series) && series.TryGetInt32(out var seriesId) && seriesId > 0
            ? seriesId
            : null;
    }

    private static bool IsAdminRole(string? role) => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
