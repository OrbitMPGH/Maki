using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Reads and writes the global scanlation group lists (<see cref="SettingKeys.ScanlationGroups"/>) and
/// combines them with a series' own (<see cref="Series.PreferredGroups"/>, <see cref="Series.BlockedGroups"/>).
/// </summary>
public static class ScanlationGroupSettings
{
    private sealed record Stored(List<string>? Preferred, List<string>? Blocked);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<ScanlationGroupPreference> GlobalAsync(IAppSettings settings, CancellationToken ct)
    {
        var raw = await settings.GetAsync(SettingKeys.ScanlationGroups, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ScanlationGroupPreference.None;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(raw, Json);
            return ScanlationGroupPreference.Of(stored?.Preferred, stored?.Blocked);
        }
        catch (JsonException)
        {
            return ScanlationGroupPreference.None;
        }
    }

    public static Task SaveGlobalAsync(IAppSettings settings, ScanlationGroupPreference groups, CancellationToken ct) =>
        settings.SetAsync(SettingKeys.ScanlationGroups,
            groups.IsEmpty ? null : JsonSerializer.Serialize(new Stored([.. groups.Preferred], [.. groups.Blocked]), Json), ct);

    public static ScanlationGroupPreference OfSeries(Series series) =>
        ScanlationGroupPreference.Of(series.PreferredGroups, series.BlockedGroups);

    /// <summary>What applies to a series: its own lists combined with the global ones.</summary>
    public static async Task<ScanlationGroupPreference> ForSeriesAsync(
        IAppSettings settings, Series series, CancellationToken ct) =>
        ScanlationGroupPreference.Combine(await GlobalAsync(settings, ct), OfSeries(series));

    /// <summary>
    /// As <see cref="ForSeriesAsync(IAppSettings, Series, CancellationToken)"/>, read by id. Null for a source
    /// that lists one upload per chapter, where there is nothing to choose and nothing to read.
    /// </summary>
    public static async Task<ScanlationGroupPreference?> ForSeriesAsync(
        IAppSettings settings, MakiDbContext db, ISource source, int seriesId, CancellationToken ct)
    {
        if (source is not IScanlationGroupSource)
        {
            return null;
        }

        var own = await db.Series.AsNoTracking()
            .Where(s => s.Id == seriesId)
            .Select(s => new { s.PreferredGroups, s.BlockedGroups })
            .FirstOrDefaultAsync(ct);
        return ScanlationGroupPreference.Combine(await GlobalAsync(settings, ct),
            ScanlationGroupPreference.Of(own?.PreferredGroups, own?.BlockedGroups));
    }
}
