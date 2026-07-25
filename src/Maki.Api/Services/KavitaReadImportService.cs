using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Scrobbling;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One-off import of read status out of Kavita, so a library that has been read there doesn't
/// start from zero in Maki's own reader.
/// <para>
/// Deliberately invisible to Rewind. Those chapters were read on dates Kavita no longer tells us,
/// and stamping them with today's date would drop a whole back catalogue onto a single day of the
/// year in review. Rewind counts only reading Maki observed happening: the scrobble job's Kavita
/// deltas and the built-in reader. The import therefore writes
/// <see cref="ChapterProgress"/> rows and silently raises the high-water mark
/// (<see cref="ReadingProgressService.ImportSilentAsync"/>), and emits no
/// <see cref="StatsEvent"/> — raising the mark is still required, or the first genuine read after
/// an import would emit a delta of hundreds.
/// </para>
/// </summary>
public class KavitaReadImportService(
    IServiceScopeFactory scopeFactory,
    SettingsService settings,
    KavitaClient kavita,
    ILogger<KavitaReadImportService> logger)
{
    /// <summary>Kavita marks specials/uncounted items with huge sentinel numbers.</summary>
    private const double Sentinel = 10000;

    public record ImportResult(int SeriesMatched, int ChaptersMarked, int SeriesUnmatched);

    public sealed class ImportState
    {
        public bool Running { get; set; }
        public DateTime? FinishedAt { get; set; }
        public ImportResult? Result { get; set; }
        public string? Error { get; set; }
    }

    private readonly SemaphoreSlim _lock = new(1, 1);

    public ImportState State { get; } = new();

    /// <summary>Starts an import unless one is already running. Returns false if it was busy.</summary>
    public bool Start()
    {
        if (!_lock.Wait(0))
        {
            return false;
        }

        State.Running = true;
        State.Error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                State.Result = await RunAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                State.Error = e.Message;
                logger.LogWarning(e, "Kavita read-status import failed");
            }
            finally
            {
                State.Running = false;
                State.FinishedAt = DateTime.UtcNow;
                _lock.Release();
            }
        });

        return true;
    }

    private async Task<ImportResult> RunAsync(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Kavita is not configured (Settings → Kavita)");
        }

        var index = await BuildLibraryIndexAsync(ct);
        var kavitaSeries = await kavita.GetAllSeriesAsync(url, apiKey, ct);

        int matched = 0, marked = 0, unmatched = 0;

        foreach (var series in kavitaSeries)
        {
            ct.ThrowIfCancellationRequested();

            var title = series.Name ?? "";
            if (!index.TryGetValue(ScrobbleMatching.NormalizeTitle(title), out var localSeriesId) &&
                (series.LocalizedName is null ||
                 !index.TryGetValue(ScrobbleMatching.NormalizeTitle(series.LocalizedName), out localSeriesId)))
            {
                unmatched++;
                continue;
            }

            List<KavitaProgress.KavitaVolumeDto> volumes;
            try
            {
                volumes = await kavita.GetVolumesAsync(url, apiKey, series.Id, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning("Could not read Kavita progress for '{Title}': {Error}", title, e.Message);
                continue;
            }

            var readNumbers = ReadChapterNumbers(volumes);

            matched++;
            if (readNumbers.Count == 0)
            {
                continue;
            }

            marked += await MarkAsync(localSeriesId, readNumbers, ct);

            var progress = KavitaProgress.Compute(volumes);
            using var scope = scopeFactory.CreateScope();
            var reading = scope.ServiceProvider.GetRequiredService<ReadingProgressService>();
            await reading.ImportSilentAsync(localSeriesId, series.Id, title,
                progress.MaxChapter, progress.MaxVolume, ct);
        }

        logger.LogInformation(
            "Kavita read import: {Matched} series matched, {Marked} chapters marked read, {Unmatched} unmatched",
            matched, marked, unmatched);
        return new ImportResult(matched, marked, unmatched);
    }

    /// <summary>
    /// Chapter numbers Kavita reports as <em>fully</em> read. A partially-read chapter is not a
    /// read one, and Kavita tags specials/uncounted entries with huge sentinel numbers that must
    /// never be matched against a real local chapter number.
    /// </summary>
    internal static HashSet<decimal> ReadChapterNumbers(List<KavitaProgress.KavitaVolumeDto> volumes) =>
        volumes
            .SelectMany(v => v.Chapters ?? [])
            .Where(c => !c.IsSpecial && c.Pages > 0 && c.PagesRead >= c.Pages &&
                        c.Number is { } n && n > 0 && n < Sentinel)
            .Select(c => (decimal)c.Number!.Value)
            .ToHashSet();

    /// <summary>Marks every downloaded local chapter whose number Kavita reports as fully read.</summary>
    internal async Task<int> MarkAsync(int seriesId, HashSet<decimal> readNumbers, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var chapters = await db.Chapters
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null && c.Number != null)
            .Select(c => new { c.Id, c.Number })
            .ToListAsync(ct);

        var targets = chapters.Where(c => readNumbers.Contains(c.Number!.Value)).Select(c => c.Id).ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var existing = await db.ChapterProgress
            .Where(p => p.SeriesId == seriesId && targets.Contains(p.ChapterId))
            .ToListAsync(ct);
        var byChapter = existing.ToDictionary(p => p.ChapterId);

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var chapterId in targets)
        {
            if (byChapter.TryGetValue(chapterId, out var row))
            {
                if (row.Completed)
                {
                    continue; // already read here; never un-complete
                }

                row.Completed = true;
                row.UpdatedAt = now;
            }
            else
            {
                // PageCount stays 0: filling it would mean opening every archive in the library.
                // The reader writes the real count the first time the chapter is opened, and
                // nothing reads PageCount for a chapter that is already complete.
                db.ChapterProgress.Add(new ChapterProgress
                {
                    SeriesId = seriesId,
                    ChapterId = chapterId,
                    PageIndex = 0,
                    PageCount = 0,
                    Completed = true,
                    StartedAt = now,
                    UpdatedAt = now,
                });
            }

            added++;
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Normalized title (and folder name) → local series id, for reverse-matching Kavita.</summary>
    private async Task<Dictionary<string, int>> BuildLibraryIndexAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var rows = await db.Series.AsNoTracking()
            .Select(s => new { s.Id, s.Title, s.FolderName })
            .ToListAsync(ct);

        var index = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            index.TryAdd(ScrobbleMatching.NormalizeTitle(row.Title), row.Id);
            if (!string.IsNullOrWhiteSpace(row.FolderName))
            {
                index.TryAdd(ScrobbleMatching.NormalizeTitle(row.FolderName), row.Id);
            }
        }

        return index;
    }
}
