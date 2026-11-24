using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maki.Core.Configuration;

/// <summary>
/// The per-user keyboard shortcut overrides stored under <c>ui.shortcuts</c>: action id to the keys
/// that trigger it. Only changed actions are stored, and an empty list means the action has no key.
/// The client owns the action ids and the key names; the server only bounds the shape.
/// </summary>
public static partial class ShortcutBindings
{
    public const int MaxActions = 64;
    public const int MaxKeysPerAction = 4;
    public const int MaxKeyLength = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Empty { get; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The stored overrides. Unset or unreadable means none.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>?>>(json, Json);
            return Normalize(raw) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>The overrides trimmed and deduplicated, or null when they break a limit.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Normalize(
        IReadOnlyDictionary<string, List<string>?>? bindings)
    {
        if (bindings is null) return Empty;
        if (bindings.Count > MaxActions) return null;

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (action, keys) in bindings)
        {
            if (!ActionIdRegex().IsMatch(action)) return null;
            var cleaned = (keys ?? []).Select(k => k?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
            if (cleaned.Count > MaxKeysPerAction) return null;
            if (cleaned.Any(k => k.Length is 0 or > MaxKeyLength || k.Any(char.IsControl))) return null;
            result[action] = cleaned;
        }
        return result;
    }

    public static string Serialize(IReadOnlyDictionary<string, IReadOnlyList<string>> bindings) =>
        bindings.Count == 0 ? "" : JsonSerializer.Serialize(bindings, Json);

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9.]{0,47}$")]
    private static partial Regex ActionIdRegex();
}
