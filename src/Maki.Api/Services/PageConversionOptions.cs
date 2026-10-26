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

    /// <summary>Null while conversion is off.</summary>
    public static async Task<PageConversionSettings?> LoadAsync(MakiDbContext db, CancellationToken ct) =>
        (await ReadAsync(db, ct)).Active;

    /// <summary>
    /// Records a conversion on the row whose bytes it describes. <paramref name="acquiredFormat"/> is
    /// the format the row was measured in before; the result's own reading stands in when it was never measured.
    /// </summary>
    public static void Apply(ChapterFile file, ArchiveConversionResult result, string? acquiredFormat, DateTime now)
    {
        file.ResetConversion();
        file.ConversionCheckedAtUtc = now;
        if (result.Outcome != ArchiveConversionOutcome.Converted)
        {
            return;
        }

        file.OriginalImageFormat = acquiredFormat ?? result.SourceFormat;
        file.ConversionSavedBytes = result.SavedBytes;
        file.ImageFormat = result.ResultFormat;
    }
}
