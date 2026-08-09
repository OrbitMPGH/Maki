using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Appends rows to the activity log. <see cref="Record"/> only stages the row on the
/// shared scoped context — use it when the caller's own SaveChanges is about to run anyway;
/// <see cref="RecordAsync"/> saves immediately for call sites with no save of their own left.
/// <para>
/// Both are the gate for <see cref="IncognitoMode.Full"/>: a fully-incognito series is dropped
/// here, silently, so every caller stays free of the check.
/// </para>
/// </summary>
public class StatsEventService(MakiDbContext db)
{
    /// <param name="seriesKey">
    /// Overrides the key looked up from <paramref name="seriesId"/>. Needed by the one caller that
    /// records an event for a series that no longer exists — <c>SeriesController.Delete</c> snapshots
    /// the key before the row goes, so the removal event lands under the same identity as the reads
    /// that preceded it and the add that may follow.
    /// </param>
    public void Record(StatsEventType type, int? seriesId, string seriesTitle, int value = 1,
        int? kavitaSeriesId = null, string? payloadJson = null, string? seriesKey = null)
    {
        if (seriesId is int sid)
        {
            var row = db.Series.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.Id == sid)
                .Select(s => new
                {
                    s.Incognito, s.Title, s.MangaBakaId, s.MangaDexUuid, s.AniListId, s.MalId
                })
                .FirstOrDefault();

            if (row?.Incognito == IncognitoMode.Full)
            {
                return;
            }

            seriesKey ??= row is null
                ? null
                : SeriesIdentity.For(new Series
                {
                    Title = row.Title,
                    MangaBakaId = row.MangaBakaId,
                    MangaDexUuid = row.MangaDexUuid,
                    AniListId = row.AniListId,
                    MalId = row.MalId
                });
        }

        // No series row to read ids off, so the title is all the identity there is. Matches what
        // adoption falls back on for a re-added series with no provider ids.
        seriesKey ??= SeriesIdentity.ForTitle(seriesTitle);

        db.StatsEvents.Add(new StatsEvent
        {
            Type = type,
            Timestamp = DateTime.UtcNow,
            SeriesId = seriesId,
            KavitaSeriesId = kavitaSeriesId,
            SeriesKey = seriesKey,
            SeriesTitle = seriesTitle,
            Value = value,
            PayloadJson = payloadJson
        });
    }

    public async Task RecordAsync(StatsEventType type, int? seriesId, string seriesTitle, int value = 1,
        int? kavitaSeriesId = null, string? payloadJson = null, string? seriesKey = null,
        CancellationToken ct = default)
    {
        Record(type, seriesId, seriesTitle, value, kavitaSeriesId, payloadJson, seriesKey);
        await db.SaveChangesAsync(ct);
    }
}
