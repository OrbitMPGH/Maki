using System.Text.Json;

namespace Maki.Core.Inbox;

/// <summary>
/// A user's Web Push filter: which inbox event types also reach their devices as a push. Stored
/// under <c>notifications.push</c> as a JSON array of event type keys, the same keys the inbox prefs
/// blob uses. It narrows the inbox and never widens it: a type switched off in the inbox is not
/// pushed whatever this says.
/// </summary>
public static class PushPrefs
{
    /// <summary>What an unset filter means: the two events a reader away from the app wants to hear about.</summary>
    public static readonly InboxEventType[] Defaults = [InboxEventType.NewChapterAvailable, InboxEventType.ChapterDownloaded];

    /// <summary>Reads a stored filter. Null, blank or unparseable is <see cref="Defaults"/>; unknown keys are dropped.</summary>
    public static IReadOnlySet<InboxEventType> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Defaults.ToHashSet();
        }

        try
        {
            return FromKeys(JsonSerializer.Deserialize<string[]>(json) ?? []);
        }
        catch (JsonException)
        {
            return Defaults.ToHashSet();
        }
    }

    public static IReadOnlySet<InboxEventType> FromKeys(IEnumerable<string> keys)
    {
        var wanted = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return InboxEventTypes.All.Where(t => wanted.Contains(InboxEventTypes.Key(t))).ToHashSet();
    }

    /// <summary>The stored form, or null for the defaults so an untouched filter keeps no row.</summary>
    public static string? Serialize(IReadOnlySet<InboxEventType> types) =>
        types.SetEquals(Defaults)
            ? null
            : JsonSerializer.Serialize(InboxEventTypes.All.Where(types.Contains).Select(InboxEventTypes.Key));

    public static string[] Keys(IReadOnlySet<InboxEventType> types) =>
        InboxEventTypes.All.Where(types.Contains).Select(InboxEventTypes.Key).ToArray();
}
