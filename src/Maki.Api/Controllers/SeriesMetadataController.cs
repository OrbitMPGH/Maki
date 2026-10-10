using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Hand-set metadata. Editing a field stores the value and locks it against metadata refreshes;
/// resetting unlocks it and refreshes the series so the provider's value comes back. Locks live on
/// the series, so they apply to everyone who can see it. The poster upload is on
/// <see cref="MediaCoverController"/>, beside the route that serves it.
/// </summary>
[ApiController]
[Route("api/v1/series/{id:int}/metadata")]
public class SeriesMetadataController(
    ILocalizer localizer,
    MakiDbContext db,
    SeriesMetadataRefreshService metadataRefresh,
    KavitaScanService kavitaScans) : ControllerBase
{
    internal const int MaxTitleLength = 500;
    internal const int MaxOverviewLength = 20_000;
    internal const int MaxGenres = 50;
    internal const int MaxGenreLength = 100;
    internal const int MaxCount = 100_000;

    private const SeriesMetadataField Editable =
        SeriesMetadataField.Title | SeriesMetadataField.Overview | SeriesMetadataField.Status |
        SeriesMetadataField.TotalChapters | SeriesMetadataField.TotalVolumes | SeriesMetadataField.Genres;

    /// <param name="Fields">
    /// The fields to set, by their camelCase name. Only these are read from the request, so a null
    /// <see cref="TotalChapters"/> named here clears the count, and one not named is left alone.
    /// </param>
    public record EditMetadataRequest(
        IReadOnlyList<string>? Fields,
        string? Title = null,
        string? Overview = null,
        string? Status = null,
        int? TotalChapters = null,
        int? TotalVolumes = null,
        IReadOnlyList<string>? Genres = null);

    public record ResetMetadataRequest(IReadOnlyList<string>? Fields);

    /// <param name="Refreshed">Whether the provider answered after a reset. False leaves the unlocked value as it was until the next refresh.</param>
    public record MetadataStateDto(IReadOnlyList<string> LockedFields, bool Refreshed = false);

    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut]
    public async Task<IActionResult> Edit(int id, [FromBody] EditMetadataRequest request, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (ParseFields(request.Fields, Editable) is not { } fields)
        {
            return this.Fail(localizer, "error.metadata.unknownField");
        }

        if (fields == SeriesMetadataField.None)
        {
            return this.Fail(localizer, "error.metadata.noFields");
        }

        if (Validate(request, fields) is { } error)
        {
            return this.Fail(localizer, error, new { max = MaxCount });
        }

        Apply(series, request, fields);
        series.LockedFields |= fields;
        await db.SaveChangesAsync(ct);
        return Ok(new MetadataStateDto(SeriesDto.LockedFieldNames(series.LockedFields)));
    }

    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(int id, [FromBody] ResetMetadataRequest request, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (ParseFields(request.Fields, Editable | SeriesMetadataField.Cover) is not { } fields)
        {
            return this.Fail(localizer, "error.metadata.unknownField");
        }

        if (fields == SeriesMetadataField.None)
        {
            return this.Fail(localizer, "error.metadata.noFields");
        }

        series.LockedFields &= ~fields;
        var refreshed = await metadataRefresh.RefreshAsync(
            series, includeCover: (fields & SeriesMetadataField.Cover) != 0, restore: fields, ct);
        await db.SaveChangesAsync(ct);

        if (refreshed && series.RootFolder is { } rootFolder)
        {
            kavitaScans.QueuePush(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        }

        return Ok(new MetadataStateDto(SeriesDto.LockedFieldNames(series.LockedFields), refreshed));
    }

    /// <returns>The named fields, or null when a name is unknown or not allowed here.</returns>
    internal static SeriesMetadataField? ParseFields(IReadOnlyList<string>? names, SeriesMetadataField allowed)
    {
        var fields = SeriesMetadataField.None;
        foreach (var name in names ?? [])
        {
            var field = Enum.GetValues<SeriesMetadataField>()
                .FirstOrDefault(f => f != SeriesMetadataField.None &&
                                     string.Equals(SeriesDto.MetadataFieldName(f), name, StringComparison.OrdinalIgnoreCase));
            if (field == SeriesMetadataField.None || (allowed & field) != field)
            {
                return null;
            }

            fields |= field;
        }

        return fields;
    }

    private static bool Has(SeriesMetadataField fields, SeriesMetadataField field) => (fields & field) == field;

    private static string? Validate(EditMetadataRequest request, SeriesMetadataField fields)
    {
        if (Has(fields, SeriesMetadataField.Title))
        {
            var title = request.Title?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                return "error.metadata.titleRequired";
            }

            if (title.Length > MaxTitleLength)
            {
                return "error.metadata.tooLong";
            }
        }

        if (Has(fields, SeriesMetadataField.Overview) && request.Overview?.Trim().Length > MaxOverviewLength)
        {
            return "error.metadata.tooLong";
        }

        if (Has(fields, SeriesMetadataField.Status) && ParseStatus(request.Status) is null)
        {
            return "error.metadata.invalidStatus";
        }

        if ((Has(fields, SeriesMetadataField.TotalChapters) && request.TotalChapters is < 0 or > MaxCount) ||
            (Has(fields, SeriesMetadataField.TotalVolumes) && request.TotalVolumes is < 0 or > MaxCount))
        {
            return "error.metadata.invalidCount";
        }

        if (Has(fields, SeriesMetadataField.Genres))
        {
            var genres = CleanGenres(request.Genres);
            if (genres.Count > MaxGenres || genres.Any(g => g.Length > MaxGenreLength))
            {
                return "error.metadata.tooLong";
            }
        }

        return null;
    }

    private static void Apply(Series series, EditMetadataRequest request, SeriesMetadataField fields)
    {
        if (Has(fields, SeriesMetadataField.Title))
        {
            var title = request.Title!.Trim();
            series.Title = title;
            series.SortTitle = SeriesMetadataMapper.SortTitleFor(title);
        }

        if (Has(fields, SeriesMetadataField.Overview))
        {
            series.Overview = string.IsNullOrWhiteSpace(request.Overview) ? null : request.Overview.Trim();
        }

        if (Has(fields, SeriesMetadataField.Status))
        {
            series.Status = ParseStatus(request.Status)!.Value;
        }

        if (Has(fields, SeriesMetadataField.TotalChapters))
        {
            series.TotalChapters = request.TotalChapters;
        }

        if (Has(fields, SeriesMetadataField.TotalVolumes))
        {
            series.TotalVolumes = request.TotalVolumes;
        }

        if (Has(fields, SeriesMetadataField.Genres))
        {
            series.Genres = CleanGenres(request.Genres);
        }
    }

    /// <summary>By name only: <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> would also take "7".</summary>
    private static SeriesStatus? ParseStatus(string? value) =>
        Enum.GetValues<SeriesStatus>()
            .Where(s => string.Equals(s.ToString(), value, StringComparison.OrdinalIgnoreCase))
            .Select(s => (SeriesStatus?)s)
            .FirstOrDefault();

    private static List<string> CleanGenres(IReadOnlyList<string>? genres) =>
        (genres ?? [])
        .Select(g => g.Trim())
        .Where(g => g.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
