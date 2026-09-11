using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// A personal estimate of the active reading time left in a series. Scrolling and paged reading
/// use separate histories because a long-strip chapter and a page-turn chapter are not comparable
/// units even when both happen to contain the same number of images.
/// </summary>
public record ReadingTimeEstimate(
    int Seconds,
    int RemainingChapters,
    string Style,
    int SampleChapters,
    bool SeriesSpecific);

public class ReadingTimeEstimateService(MakiDbContext db)
{
    private const int MinimumSamples = 3;
    private const int MaximumSamples = 100;

    public async Task<ReadingTimeEstimate?> EstimateAsync(
        int seriesId,
        int totalChapters,
        int readChapters,
        string readerMode,
        CancellationToken ct = default)
    {
        var remaining = Math.Max(0, totalChapters - readChapters);
        if (remaining == 0)
        {
            return null;
        }

        var scrolling = readerMode == ReaderPrefsSpec.ModeVertical;
        var samples = await TimedChapters()
            .Where(x => x.SeriesId == seriesId)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => x.ReadSeconds)
            .Take(MaximumSamples)
            .ToListAsync(ct);
        var seriesSpecific = samples.Count >= MinimumSamples;

        if (!seriesSpecific)
        {
            // Reader mode was not stored with old progress rows. Series type is the durable signal
            // available for that history: manhua/manhwa are the long-strip cohort, while manga and
            // the default/unknown types are paged. A current series override still chooses which
            // cohort is used through the resolved readerMode above.
            samples = await TimedChapters()
                .Join(db.Series,
                    progress => progress.SeriesId,
                    series => series.Id,
                    (progress, series) => new { progress.ReadSeconds, progress.UpdatedAt, series.Type })
                .Where(x => scrolling
                    ? x.Type == SeriesTypes.Manhua || x.Type == SeriesTypes.Manhwa
                    : x.Type != SeriesTypes.Manhua && x.Type != SeriesTypes.Manhwa)
                .OrderByDescending(x => x.UpdatedAt)
                .Select(x => x.ReadSeconds)
                .Take(MaximumSamples)
                .ToListAsync(ct);
        }

        if (samples.Count < MinimumSamples)
        {
            return null;
        }

        samples.Sort();
        var middle = samples.Count / 2;
        var median = samples.Count % 2 == 0
            ? (samples[middle - 1] + samples[middle]) / 2d
            : samples[middle];
        var seconds = (int)Math.Min(int.MaxValue, Math.Round(median * remaining));

        return new ReadingTimeEstimate(
            seconds,
            remaining,
            scrolling ? "scrolling" : "paged",
            samples.Count,
            seriesSpecific);
    }

    private IQueryable<ChapterProgress> TimedChapters() =>
        db.ChapterProgress.Where(progress =>
            progress.Completed &&
            !progress.Watched &&
            progress.ReadSeconds > 0);
}
