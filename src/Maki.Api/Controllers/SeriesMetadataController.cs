using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
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
/// <para>
/// Every change, by hand or by refresh, lands in the series' change history, readable by anyone who
/// can see the series.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/series/{id:int}/metadata")]
public class SeriesMetadataController(
    ILocalizer localizer,
    MakiDbContext db,
    SeriesMetadataRefreshService metadataRefresh,
    SeriesMetadataChangeLog changeLog,
    SeriesIdentityService identity,
    ICurrentUser currentUser,
    KavitaScanService kavitaScans) : ControllerBase
{
    private const int HistoryLimit = 200;

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

    /// <param name="Field">camelCase, as in <see cref="SeriesDto.LockedFields"/>.</param>
    /// <param name="OldValue">Invariant text: a status by its enum name, counts as digits, genres comma separated. Null for the synopsis and the cover.</param>
    /// <param name="Source">"refresh" or "user".</param>
    /// <param name="UserName">Who made a user change; null for a refresh or a deleted account.</param>
    public record MetadataChangeDto(
        int Id, string Field, string? OldValue, string? NewValue, string Source, string? UserName, DateTime ChangedAt);

    /// <summary>The series' metadata changes, newest first, capped at the latest <see cref="HistoryLimit"/>.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History(int id, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id, ct))
        {
            return NotFound();
        }

        var rows = await db.SeriesMetadataChanges
            .AsNoTracking()
            .Where(c => c.SeriesId == id)
            .OrderByDescending(c => c.ChangedAtUtc)
            .ThenByDescending(c => c.Id)
            .Take(HistoryLimit)
            .Select(c => new
            {
                Change = c,
                UserName = db.Users.Where(u => u.Id == c.UserId).Select(u => u.DisplayName ?? u.UserName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Ok(rows.Select(r => new MetadataChangeDto(
            r.Change.Id,
            SeriesDto.MetadataFieldName(r.Change.Field),
            r.Change.OldValue,
            r.Change.NewValue,
            r.Change.Source == MetadataChangeSource.User ? "user" : "refresh",
            r.UserName,
            DateTime.SpecifyKind(r.Change.ChangedAtUtc, DateTimeKind.Utc))));
    }

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

        var before = SeriesMetadataChangeLog.Snapshot.Of(series);
        var oldKey = SeriesIdentity.For(series);
        var overview = series.Overview;
        var genres = string.Join(", ", series.Genres);
        Apply(series, request, fields);
        series.LockedFields |= fields;

        var userId = currentUser.UserId;
        const MetadataChangeSource user = MetadataChangeSource.User;
        changeLog.Record(series, SeriesMetadataField.Title, before.Title, series.Title, user, userId);
        changeLog.RecordStatus(series, before.Status, series.Status, user, userId);
        changeLog.Record(series, SeriesMetadataField.TotalChapters,
            SeriesMetadataChangeLog.Text(before.TotalChapters), SeriesMetadataChangeLog.Text(series.TotalChapters), user, userId);
        changeLog.Record(series, SeriesMetadataField.TotalVolumes,
            SeriesMetadataChangeLog.Text(before.TotalVolumes), SeriesMetadataChangeLog.Text(series.TotalVolumes), user, userId);
        changeLog.Record(series, SeriesMetadataField.Genres, genres, string.Join(", ", series.Genres), user, userId);
        if (!string.Equals(overview, series.Overview, StringComparison.Ordinal))
        {
            changeLog.RecordReplaced(series, SeriesMetadataField.Overview, userId);
        }

        await SaveAsync(series, oldKey, ct);
        await changeLog.PublishAsync(ct);
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

        var oldKey = SeriesIdentity.For(series);
        series.LockedFields &= ~fields;
        var refreshed = await metadataRefresh.RefreshAsync(
            series, includeCover: (fields & SeriesMetadataField.Cover) != 0, restore: fields, ct);
        await SaveAsync(series, oldKey, ct);
        await changeLog.PublishAsync(ct);

        if (refreshed && series.RootFolder is { } rootFolder)
        {
            kavitaScans.QueuePush(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        }

        return Ok(new MetadataStateDto(SeriesDto.LockedFieldNames(series.LockedFields), refreshed));
    }

    /// <summary>
    /// Saves, and when the change moved the series' stats identity (a title edit on a series with no
    /// provider ids) re-keys its history in the same transaction, so the save and the re-key land together.
    /// </summary>
    private async Task SaveAsync(Series series, string oldKey, CancellationToken ct)
    {
        if (SeriesIdentity.For(series) == oldKey)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await identity.RekeyAsync(series, oldKey, ct);
        await transaction.CommitAsync(ct);
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
