using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>The page conversion settings, and how a conversion result is written onto a <see cref="ChapterFile"/>.</summary>
public static class PageConversionOptions
{
    private static readonly string[] Keys =
    [
        SettingKeys.PageConversionFormat, SettingKeys.PageConversionStop, SettingKeys.PageConversionPngLossless
    ];

    public sealed record Stored(PageConversionFormat? Format, PageConversionStop Stop, bool PngLossless)
    {
        public PageConversionSettings? Active => Format is { } format
            ? new PageConversionSettings(format, Stop, PngLossless)
            : null;
    }

    /// <summary>Read straight from the table, so the download processor needs no settings service of its own.</summary>
    public static async Task<Stored> ReadAsync(MakiDbContext db, CancellationToken ct)
    {
        var values = await db.AppConfig.AsNoTracking()
            .Where(c => Keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
        return new Stored(
            PageConversionSettings.ParseFormat(values.GetValueOrDefault(SettingKeys.PageConversionFormat)),
            PageConversionSettings.ParseStop(values.GetValueOrDefault(SettingKeys.PageConversionStop)) ?? PageConversionSettings.DefaultStop,
            string.Equals(values.GetValueOrDefault(SettingKeys.PageConversionPngLossless), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The stored conversion limit, or null for the default; a value out of range reads as unset.</summary>
    public static int? ParseParallel(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit) &&
        limit >= 1 && limit <= ImageWorkGate.MaxConversionLimit
            ? limit
            : null;

    /// <summary>Null while conversion is off.</summary>
    public static async Task<PageConversionSettings?> LoadAsync(MakiDbContext db, CancellationToken ct) =>
        (await ReadAsync(db, ct)).Active;

    /// <summary>The settings and the rules together; null while conversion is off.</summary>
    public static async Task<PageConversionPlan?> LoadPlanAsync(MakiDbContext db, CancellationToken ct)
    {
        var stored = await ReadAsync(db, ct);
        if (stored.Format is not { } format)
        {
            return null;
        }

        return new PageConversionPlan(format, stored.Stop, stored.PngLossless, await LoadRulesAsync(db, ct));
    }

    /// <summary>The rules in order. One whose conditions no longer parse is left out rather than read as matching everything.</summary>
    public static async Task<IReadOnlyList<PlanRule>> LoadRulesAsync(MakiDbContext db, CancellationToken ct)
    {
        var rows = await db.PageConversionRules.AsNoTracking().OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync(ct);
        return rows
            .Where(r => r.Skip || r.Stop is not null)
            .Select(r => PageConversionConditions.TryParse(r.ConditionsJson, out var conditions)
                ? new PlanRule(r.Order, r.Name, conditions, r.Skip, r.Skip ? null : r.Stop)
                : null)
            .OfType<PlanRule>()
            .ToList();
    }

    /// <summary>
    /// Records a check on the row whose bytes it describes: a conversion result, or null when the plan
    /// said to leave the file alone. <paramref name="acquiredFormat"/> is the format the row was measured
    /// in before; the result's own reading stands in when it was never measured.
    /// </summary>
    public static void Apply(ChapterFile file, ArchiveConversionResult? result, string? acquiredFormat, string checkedWith,
        DateTime now)
    {
        file.ResetConversion();
        file.ConversionCheckedAtUtc = now;
        file.CheckedWith = checkedWith;
        if (result?.Outcome != ArchiveConversionOutcome.Converted)
        {
            return;
        }

        file.OriginalImageFormat = acquiredFormat ?? result.SourceFormat;
        file.ConversionSavedBytes = result.SavedBytes;
        file.ImageFormat = result.ResultFormat;
    }
}
