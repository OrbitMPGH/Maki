using System.Text.Json;

namespace Maki.Core.Inbox;

/// <summary>
/// Which in-app notifications a user wants, and whether they want them to interrupt.
/// <para>
/// Same discipline as <see cref="Maki.Core.Configuration.HomeLayoutSpec"/>: serialize only through
/// <see cref="Json"/>, never rename a key. A name mismatch does not throw, it silently yields the
/// default, so a rename degrades into "everyone's preferences were forgotten" rather than an error.
/// </para>
/// </summary>
/// <param name="Types">
/// Keyed by <see cref="InboxEventTypes.Key"/>. Absent means "this build's default", not "off" — see
/// <see cref="Merge"/>.
/// </param>
/// <param name="Toasts">
/// Whether an arriving notification also pops a toast. Separate from the per-type switches because
/// the question is different: a user can want the record without wanting the interruption.
/// </param>
public record InboxPrefsSpec(
    IReadOnlyDictionary<string, bool>? Types = null,
    bool Toasts = true)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static InboxPrefsSpec Default => new InboxPrefsSpec().Merge();

    /// <summary>
    /// Reconciles a stored spec with the event types this build knows:
    /// <list type="bullet">
    /// <item>keys this build no longer knows are dropped;</item>
    /// <item>keys the stored spec has never seen take this build's default — <b>on</b> for almost
    /// everything, so a release adding an event type doesn't require every user to go opt in;</item>
    /// <item>an explicitly stored value always wins, including an explicit false on a type that
    /// defaults on.</item>
    /// </list>
    /// </summary>
    public InboxPrefsSpec Merge()
    {
        var merged = new Dictionary<string, bool>(InboxEventTypes.All.Length, StringComparer.OrdinalIgnoreCase);

        foreach (var type in InboxEventTypes.All)
        {
            var key = InboxEventTypes.Key(type);
            merged[key] = Types is not null && Types.TryGetValue(key, out var stored)
                ? stored
                : !InboxEventTypes.DefaultsOff(type);
        }

        return this with { Types = merged };
    }

    /// <summary>
    /// Whether a type is wanted. Falls back to the build default for an unmerged spec, so callers
    /// cannot accidentally read "absent" as "off".
    /// </summary>
    public bool Wants(InboxEventType type) =>
        Types is not null && Types.TryGetValue(InboxEventTypes.Key(type), out var enabled)
            ? enabled
            : !InboxEventTypes.DefaultsOff(type);

    /// <summary>Reads a stored blob; null, blank or unparseable falls back to defaults.</summary>
    public static InboxPrefsSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return (JsonSerializer.Deserialize<InboxPrefsSpec>(json, Json) ?? new InboxPrefsSpec()).Merge();
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static string Serialize(InboxPrefsSpec spec) => JsonSerializer.Serialize(spec.Merge(), Json);
}
