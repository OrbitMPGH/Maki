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
/// <see cref="ChapterProgress"/> rows (through <see cref="ExternalReadSyncService"/>) and silently
/// raises the high-water mark (<see cref="ReadingProgressService.ImportSilentAsync"/>), and emits no
/// <see cref="StatsEvent"/> — raising the mark is still required, or the first genuine read after
/// an import would emit a delta of hundreds.
/// </para>
/// <para>
/// Only ever needed for the back catalogue: the recurring scrobble tick marks chapters through the
/// same service, so ongoing Kavita reading arrives without running this.
/// </para>
/// </summary>
public class KavitaReadImportService(
    IServiceScopeFactory scopeFactory,
    SettingsService settings,
    KavitaClient kavita,
    ExternalReadSyncService externalReads,
    KavitaUserResolver kavitaUser,
    ILogger<KavitaReadImportService> logger)
{
    public record ImportResult(int SeriesMatched, int ChaptersMarked, int SeriesUnmatched);

    /// <summary>
    /// A failure this service worded itself, as a catalogue key rather than rendered text: this is a
    /// singleton with no <c>ILocalizer</c>; the caller renders it with the request's own localizer
    /// when it reads <see cref="ImportState"/>.
    /// </summary>
    private sealed class KavitaImportError(string key) : Exception
    {
        public string Key { get; } = key;
    }

    public sealed class ImportState
    {
        public bool Running { get; set; }
        public DateTime? FinishedAt { get; set; }
        public ImportResult? Result { get; set; }

        /// <summary>Set together with <see cref="RawError"/> always null. See <see cref="KavitaImportError"/>.</summary>
        public string? ErrorKey { get; set; }

        /// <summary>
        /// Set together with <see cref="ErrorKey"/> always null: text from outside Maki (an HTTP
        /// failure talking to Kavita, an unexpected exception), left as-is like every other raw
        /// <c>ex.Message</c> here rather than half-converted behind a generic key.
        /// </summary>
        public string? RawError { get; set; }
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
        State.ErrorKey = null;
        State.RawError = null;
        _ = Task.Run(async () =>
        {
            try
            {
                State.Result = await RunAsync(CancellationToken.None);
            }
            catch (KavitaImportError ke)
            {
                State.ErrorKey = ke.Key;
                logger.LogWarning(ke, "Kavita read-status import failed");
            }
            catch (Exception e)
            {
                State.RawError = e.Message;
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
            throw new KavitaImportError("error.reader.kavitaNotConfigured");
        }

        // One import for one user, because Kavita is one account — see KavitaUserResolver. Attributing
        // the back catalogue to the wrong reader would be worse than not importing it, so this throws
        // rather than falling back to "whoever asked".
        var userId = await kavitaUser.ResolveAsync(ct)
                     ?? throw new KavitaImportError("error.reader.kavitaNoBoundUser");

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

            var readNumbers = ExternalReadSyncService.ReadChapterNumbers(volumes);

            matched++;
            if (readNumbers.Count == 0)
            {
                continue;
            }

            marked += await externalReads.MarkAsync(userId, localSeriesId, readNumbers, ct);

            var progress = KavitaProgress.Compute(volumes);
            using var scope = scopeFactory.CreateScope();
            var reading = scope.ServiceProvider.GetRequiredService<ReadingProgressService>();
            await reading.ImportSilentAsync(userId, localSeriesId, series.Id, title,
                progress.MaxChapter, progress.MaxVolume, ct);
        }

        logger.LogInformation(
            "Kavita read import: {Matched} series matched, {Marked} chapters marked read, {Unmatched} unmatched",
            matched, marked, unmatched);
        return new ImportResult(matched, marked, unmatched);
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
