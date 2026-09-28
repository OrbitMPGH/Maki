using System.Text.Json;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;

namespace Maki.Core.Configuration;

/// <summary>
/// One tracker's import list settings for one user. Stored inside the per-user
/// <see cref="SettingKeys.ImportListPrefs"/> blob, keyed by tracker name. Serialize only through
/// <see cref="ImportListPrefs.Json"/>; a renamed property silently reads back as its default.
/// </summary>
/// <param name="Statuses">Names of <see cref="ScrobbleStatus"/>: Reading, PlanToRead, Completed.</param>
/// <param name="RootFolderId">Null means the first root folder the user can see.</param>
/// <param name="MonitorNewItems">A <see cref="NewChapterMonitorMode"/> name.</param>
public record ImportListTrackerPrefs(
    bool Enabled = false,
    IReadOnlyList<string>? Statuses = null,
    int? RootFolderId = null,
    bool Monitored = true,
    string MonitorNewItems = ImportListTrackerPrefs.DefaultMonitorNewItems,
    int MaxPerRun = ImportListTrackerPrefs.DefaultMaxPerRun)
{
    public const string DefaultMonitorNewItems = nameof(NewChapterMonitorMode.All);
    public const int DefaultMaxPerRun = 10;
    public const int MaxPerRunLimit = 100;

    public static readonly string[] AllowedStatuses =
    [
        nameof(ScrobbleStatus.Reading),
        nameof(ScrobbleStatus.PlanToRead),
        nameof(ScrobbleStatus.Completed),
    ];

    public static readonly string[] DefaultStatuses =
        [nameof(ScrobbleStatus.Reading), nameof(ScrobbleStatus.PlanToRead)];

    public ImportListTrackerPrefs Sanitized()
    {
        var statuses = (Statuses ?? [])
            .Select(s => AllowedStatuses.FirstOrDefault(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct()
            .ToList();
        return this with
        {
            Statuses = statuses.Count > 0 ? statuses : DefaultStatuses,
            MonitorNewItems = Enum.TryParse<NewChapterMonitorMode>(MonitorNewItems, true, out var mode)
                ? mode.ToString()
                : DefaultMonitorNewItems,
            MaxPerRun = Math.Clamp(MaxPerRun, 1, MaxPerRunLimit),
        };
    }

    public IReadOnlyList<ScrobbleStatus> ParsedStatuses() =>
        [.. (Sanitized().Statuses ?? DefaultStatuses).Select(s => Enum.Parse<ScrobbleStatus>(s))];
}

/// <summary>The whole <see cref="SettingKeys.ImportListPrefs"/> blob: tracker name to its prefs.</summary>
public static class ImportListPrefs
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Dictionary<string, ImportListTrackerPrefs> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ImportListTrackerPrefs>>(json, Json);
            return parsed is null
                ? new(StringComparer.Ordinal)
                : parsed.ToDictionary(p => p.Key, p => p.Value.Sanitized(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    public static ImportListTrackerPrefs For(IReadOnlyDictionary<string, ImportListTrackerPrefs> all, string service) =>
        all.TryGetValue(service, out var prefs) ? prefs : new ImportListTrackerPrefs().Sanitized();

    public static string Serialize(IReadOnlyDictionary<string, ImportListTrackerPrefs> all) =>
        JsonSerializer.Serialize(all.ToDictionary(p => p.Key, p => p.Value.Sanitized()), Json);
}

/// <summary>Per-user, per-tracker record of the last run, under <see cref="SettingKeys.ImportListLastRunKey"/>.</summary>
/// <param name="DumpUnavailable">Skipped because the local MangaBaka database was not downloaded yet.</param>
public record ImportListLastRun(
    DateTime At, int Added, int Requested, int Skipped, int Errors, bool DumpUnavailable = false)
{
    public static ImportListLastRun? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ImportListLastRun>(json, ImportListPrefs.Json); }
        catch (JsonException) { return null; }
    }

    public string Serialize() => JsonSerializer.Serialize(this, ImportListPrefs.Json);
}
