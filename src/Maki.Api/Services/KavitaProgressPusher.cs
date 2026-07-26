using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Opt-in write-back: after a chapter is finished in the built-in reader, mark it read in Kavita
/// too, so both apps agree.
/// <para>
/// Gated on the series having an <em>adopted</em> <see cref="ReadingState"/> row — one whose
/// KavitaSeriesId is set. That is the sharpest edge in the feature: the echo (push to Kavita,
/// read it back on the next scrobble tick) is harmless only because it lands in the same
/// forward-only row and yields a delta of zero. Pushing for a series whose Kavita counterpart
/// Maki has not matched would land the echo in a <em>different</em> row and double-count every
/// chapter into Rewind.
/// </para>
/// <para>Best-effort throughout: a failure here must never fail the reader's own progress write.</para>
/// </summary>
public class KavitaProgressPusher(
    IServiceScopeFactory scopeFactory,
    SettingsService settings,
    KavitaClient kavita,
    ILogger<KavitaProgressPusher> logger)
{
    /// <summary>Fire-and-forget: the reader's HTTP response must not wait on Kavita.</summary>
    public void QueuePush(int seriesId, decimal? chapterNumber)
    {
        if (chapterNumber is null)
        {
            return; // one-shots have no number to match against Kavita's chapter list
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PushAsync(seriesId, chapterNumber.Value, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning("Kavita progress push-back failed for series {SeriesId}: {Error}",
                    seriesId, e.Message);
            }
        });
    }

    private async Task PushAsync(int seriesId, decimal chapterNumber, CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.ReaderPushToKavita, ct) != "true")
        {
            return;
        }

        var url = await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        int kavitaSeriesId;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            // Ordered by MaxChapter for the same reason every other reader of this table is:
            // duplicates per SeriesId are legal, and an unordered First would push into a
            // different Kavita series between calls — see ReadingProgressService.PickAsync.
            var matched = await db.ReadingStates
                .Where(r => r.SeriesId == seriesId && r.KavitaSeriesId != null)
                .OrderByDescending(r => r.MaxChapter)
                .ThenByDescending(r => r.Id)
                .Select(r => r.KavitaSeriesId)
                .FirstOrDefaultAsync(ct);
            if (matched is not int id)
            {
                // Not matched to Kavita (yet). Silent: this is the normal state for a series
                // Kavita has never reported, and pushing anyway is what would double-count.
                return;
            }

            kavitaSeriesId = id;
        }

        var volumes = await kavita.GetVolumesAsync(url, apiKey, kavitaSeriesId, ct);
        var target = volumes
            .SelectMany(v => (v.Chapters ?? []).Select(c => (Volume: v, Chapter: c)))
            .FirstOrDefault(x => x.Chapter.Id > 0 && x.Chapter.Number is { } n &&
                                 Math.Abs(n - (double)chapterNumber) < 0.001);

        if (target.Chapter is null || target.Chapter.Pages <= 0)
        {
            logger.LogDebug("No Kavita chapter {Number} for series {SeriesId}", chapterNumber, seriesId);
            return;
        }

        var libraryId = await kavita.GetSeriesLibraryIdAsync(url, apiKey, kavitaSeriesId, ct) ?? 0;
        await kavita.SaveReadingProgressAsync(url, apiKey, libraryId, kavitaSeriesId,
            target.Volume.Id, target.Chapter.Id, target.Chapter.Pages, ct);

        logger.LogInformation("Marked chapter {Number} read in Kavita for series {SeriesId}",
            chapterNumber, seriesId);
    }
}
