using System.Text.Json;
using Maki.Core.Entities;

namespace Maki.Core.Inbox;

/// <summary>
/// The <see cref="SeriesNotificationMode"/> values a user may pick as their <em>global</em> default.
/// <see cref="SeriesNotificationMode.Default"/> is excluded because it would point at itself, and
/// <see cref="SeriesNotificationMode.Muted"/> because switching every series-scoped event type off
/// is what the per-type switches already do.
/// </summary>
public static class SeriesDefaults
{
    public const string All = nameof(SeriesNotificationMode.All);
    public const string Reading = nameof(SeriesNotificationMode.Reading);

    public static readonly string[] Allowed = [All, Reading];

    public static bool IsAllowed(string? value) =>
        value is not null && Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

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
/// <param name="SeriesDefault">
/// What a series set to <see cref="SeriesNotificationMode.Default"/> means for this user: "All"
/// (every series-scoped event, the behaviour every install had before this existed) or "Reading"
/// (only series they still have progress on). A per-series mode overrides it.
/// <para>
/// A string rather than the enum because this record is serialized with no enum converter, and a
/// value nothing recognises has to degrade to "All" rather than throw — read it through
/// <see cref="ResolvedSeriesDefault"/>, never by parsing this directly. Appended last: an older
/// blob simply has no such key and takes the default.
/// </para>
/// </param>
public record InboxPrefsSpec(
    IReadOnlyDictionary<string, bool>? Types = null,
    bool Toasts = true,
    string SeriesDefault = SeriesDefaults.All)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static InboxPrefsSpec Default => new InboxPrefsSpec().Merge();

    /// <summary>
    /// <see cref="SeriesDefault"/> as an enum. Anything outside <see cref="SeriesDefaults.Allowed"/>
    /// — an older blob with no such key, a hand-edited one, a value a future build wrote — reads as
    /// <see cref="SeriesNotificationMode.All"/>, the behaviour that predates the setting.
    /// </summary>
    public SeriesNotificationMode ResolvedSeriesDefault =>
        SeriesDefaults.IsAllowed(SeriesDefault)
        && Enum.TryParse<SeriesNotificationMode>(SeriesDefault, true, out var mode)
            ? mode
            : SeriesNotificationMode.All;

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

        return this with { Types = merged, SeriesDefault = ResolvedSeriesDefault.ToString() };
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
