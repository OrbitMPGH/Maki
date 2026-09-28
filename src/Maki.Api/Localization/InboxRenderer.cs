using System.Text.Json;

namespace Maki.Api.Localization;

/// <summary>
/// Turns a stored notification back into the two sentences a reader sees, in the reader's language.
/// <para>
/// Rendering happens here, at read time, rather than at the raise site, because one event fans out
/// to several people who do not necessarily share a language. Rendering once at the raise site would
/// freeze whichever language the job happened to run in, and a language change afterwards would
/// leave the bell full of the old one.
/// </para>
/// </summary>
public sealed class InboxRenderer(ILocalizer localizer)
{
    /// <summary>
    /// The rendered pair. Falls back to the stored English for a row written before notifications
    /// were keyed, which is the only reason <c>UserNotification.Title</c> and <c>.Body</c> still
    /// exist.
    /// </summary>
    /// <param name="seriesTitle">
    /// The reader's own display title for the series, or null when the row names none or it has
    /// since been deleted. Passed in rather than looked up here because the caller is already
    /// holding the series rows, and because resolving it per notification would be a query each.
    /// </param>
    public (string Title, string Body) Render(
        string locale,
        string? messageKey,
        string? paramsJson,
        string storedTitle,
        string storedBody,
        string? seriesTitle)
    {
        if (string.IsNullOrEmpty(messageKey))
        {
            return (storedTitle, storedBody);
        }

        var args = Parse(paramsJson);

        // Never stored on the row: this app resolves a series' display title per user, so somebody
        // who prefers Japanese titles sees the Japanese one here too. A row whose series is gone
        // renders the placeholder as the word the catalogue falls back to.
        args["series"] = seriesTitle ?? localizer.GetFor(locale, "inbox.unknownSeries");

        // An achievement arrives as its own key plus a tier number, not as a rendered phrase, so the
        // name and the tier are looked up here in the reader's language rather than baked in English
        // at unlock time. Tier 0 means the achievement is ungraded and has no tier to name.
        if (args.TryGetValue("achievement", out var achievement) && achievement is string key)
        {
            args["achievement"] = localizer.GetFor(locale, $"achievement.{key}.name");
            var tier = args.TryGetValue("tier", out var t) ? Convert.ToInt32(t) : 0;
            args["tier"] = tier > 0
                ? localizer.GetFor(locale, "achievement.tierSuffix",
                    new Dictionary<string, object?> { ["tier"] = localizer.GetFor(locale, $"achievement.tier.{tier}") })
                : string.Empty;
        }

        // A reason is stored as its own key, not as a rendered sentence, for the same reason the
        // row is: the download failed once, but several people may read about it in several
        // languages. Anything that is not a key we recognise is text somebody else wrote (a
        // scraper's, a torrent client's) and is passed through as it arrived.
        if (args.TryGetValue("error", out var error) && error is string { Length: > 0 } errorKey)
        {
            // Asked rather than pattern-matched on a prefix: a key the catalogue does not have comes
            // back as itself, which is exactly the right answer for text somebody else wrote.
            args["error"] = localizer.GetFor(locale, errorKey, args);
        }

        // A health check's sentence, stored as its key with its values under a `detail.` prefix (see
        // HealthMonitor.InboxDetailArgs). A row from before the checks were keyed stores its English,
        // which is not a key and comes back as itself.
        if (args.TryGetValue("detail", out var detail) && detail is string { Length: > 0 } detailKey)
        {
            var detailArgs = args
                .Where(a => a.Key.StartsWith("detail.", StringComparison.Ordinal))
                .ToDictionary(a => a.Key["detail.".Length..], a => a.Value, StringComparer.Ordinal);
            args["detail"] = localizer.GetFor(locale, detailKey, detailArgs);
        }

        var title = localizer.GetFor(locale, $"{messageKey}.title", args);
        var body = localizer.GetFor(locale, $"{messageKey}.body", args);

        // A free-text note somebody typed (an admin's reason for declining a request). It is their
        // words, in whatever language they wrote them, so it is appended rather than translated.
        if (args.TryGetValue("note", out var note) && note is string { Length: > 0 } text)
        {
            body = $"{body}. {text}";
        }

        return (title, body);
    }

    private static Dictionary<string, object?> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var into = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                into[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetString(),
                };
            }

            return into;
        }
        catch (JsonException)
        {
            // A row whose parameters cannot be read still has a message worth showing, with its
            // placeholders unfilled. Losing the whole notification would be worse.
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    /// <summary>Serializes parameters for storage. Null when there are none, so the column stays empty.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, object?>? args) =>
        args is null || args.Count == 0 ? null : JsonSerializer.Serialize(args);
}
