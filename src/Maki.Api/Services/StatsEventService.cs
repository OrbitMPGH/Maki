using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Appends rows to the Rewind activity log. <see cref="Record"/> only stages the row on the
/// shared scoped context — use it when the caller's own SaveChanges is about to run anyway;
/// <see cref="RecordAsync"/> saves immediately for call sites with no save of their own left.
/// <para>
/// Both are the gate for <see cref="IncognitoMode.Full"/>: a fully-incognito series is dropped
/// here, silently, so every caller stays free of the check.
/// </para>
/// </summary>
public class StatsEventService(MakiDbContext db)
{
    public void Record(StatsEventType type, int? seriesId, string seriesTitle, int value = 1,
        int? kavitaSeriesId = null, string? payloadJson = null)
    {
        if (seriesId is int sid && db.Series.AsNoTracking()
                .Where(s => s.Id == sid).Select(s => s.Incognito).FirstOrDefault() == IncognitoMode.Full)
        {
            return;
        }

        db.StatsEvents.Add(new StatsEvent
        {
            Type = type,
            Timestamp = DateTime.UtcNow,
            SeriesId = seriesId,
            KavitaSeriesId = kavitaSeriesId,
            SeriesTitle = seriesTitle,
            Value = value,
            PayloadJson = payloadJson
        });
    }

    public async Task RecordAsync(StatsEventType type, int? seriesId, string seriesTitle, int value = 1,
        int? kavitaSeriesId = null, string? payloadJson = null, CancellationToken ct = default)
    {
        Record(type, seriesId, seriesTitle, value, kavitaSeriesId, payloadJson);
        await db.SaveChangesAsync(ct);
    }
}
