using System.Collections.Concurrent;
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
    VolumeBoundaryService volumeBoundaries,
    KavitaUserResolver kavitaUser,
    ILogger<KavitaReadImportService> logger)
{
    public record ImportResult(
        int SeriesMatched, int ChaptersMarked, int SeriesUnmatched,
        int SeriesFailed, IReadOnlyList<string> FailedTitles);

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
        State.Result = null;
        State.FinishedAt = null;
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
        var failedTitles = new List<string>();

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
                failedTitles.Add(title);
                continue;
            }

            var progress = KavitaProgress.Compute(
                volumes, await volumeBoundaries.ForSeriesAsync(userId, localSeriesId, ct));

            matched++;
            if (progress.IsEmpty)
            {
                continue;
            }

            marked += await externalReads.MarkAsync(userId, localSeriesId, progress, ct);

            using var scope = scopeFactory.CreateScope();
            var reading = scope.ServiceProvider.GetRequiredService<ReadingProgressService>();
            await reading.ImportSilentAsync(userId, localSeriesId, series.Id, title,
                progress.MaxChapter, progress.MaxVolume, ct);
        }

        logger.LogInformation(
            "Kavita read import: {Matched} series matched, {Marked} chapters marked read, {Unmatched} unmatched, {Failed} failed",
            matched, marked, unmatched, failedTitles.Count);
        return new ImportResult(matched, marked, unmatched, failedTitles.Count, failedTitles);
    }

    /// <summary>
    /// The import for one Kavita series, run by <see cref="KavitaLiveReadSync"/> the moment Kavita
    /// reports progress on it. Writes the same rows as the full import, through the same
    /// <see cref="ExternalReadSyncService.MarkAsync"/>.
    /// <para>
    /// Does <em>not</em> raise the high-water mark. The next scrobble tick still reads this series
    /// from Kavita and records the mark delta for Rewind and the trackers; raising it here would turn
    /// that delta into zero and the reading would never reach either.
    /// </para>
    /// </summary>
    public record SeriesMarkResult(int SeriesId, int Marked);

    private static readonly TimeSpan UnmatchedFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<int, DateTime> _unmatched = new();

    /// <summary>
    /// Kavita series with no local match are remembered briefly: every page turn of one would
    /// otherwise repeat a Kavita GET and rebuild the library index just to find nothing again.
    /// </summary>
    internal bool IsKnownUnmatched(int kavitaSeriesId) =>
        _unmatched.TryGetValue(kavitaSeriesId, out var at) && DateTime.UtcNow - at < UnmatchedFor;

    public async Task<SeriesMarkResult?> MarkSeriesAsync(
        int userId, string url, string apiKey, int kavitaSeriesId, CancellationToken ct)
    {
        var localSeriesId = await AdoptedSeriesIdAsync(userId, kavitaSeriesId, ct);
        if (localSeriesId is null)
        {
            if (IsKnownUnmatched(kavitaSeriesId))
            {
                return null;
            }

            var series = await kavita.GetSeriesAsync(url, apiKey, kavitaSeriesId, ct);
            if (series is null)
            {
                _unmatched[kavitaSeriesId] = DateTime.UtcNow;
                return null;
            }

            var index = await BuildLibraryIndexAsync(ct);
            if (index.TryGetValue(ScrobbleMatching.NormalizeTitle(series.Name ?? ""), out var byName))
            {
                localSeriesId = byName;
            }
            else if (series.LocalizedName is not null &&
                     index.TryGetValue(ScrobbleMatching.NormalizeTitle(series.LocalizedName), out var byLocalized))
            {
                localSeriesId = byLocalized;
            }
            else
            {
                _unmatched[kavitaSeriesId] = DateTime.UtcNow;
                return null;
            }
        }

        _unmatched.TryRemove(kavitaSeriesId, out _);
        var volumes = await kavita.GetVolumesAsync(url, apiKey, kavitaSeriesId, ct);
        var progress = KavitaProgress.Compute(
            volumes, await volumeBoundaries.ForSeriesAsync(userId, localSeriesId.Value, ct));
        var marked = await externalReads.MarkAsync(userId, localSeriesId.Value, progress, ct);
        return new SeriesMarkResult(localSeriesId.Value, marked);
    }

    /// <summary>The local series a previous scrobble tick already tied to this Kavita series, if any.</summary>
    private async Task<int?> AdoptedSeriesIdAsync(int userId, int kavitaSeriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.ReadingStates.AsNoTracking()
            .Where(r => r.UserId == userId && r.KavitaSeriesId == kavitaSeriesId && r.SeriesId != null)
            .OrderByDescending(r => r.MaxChapter)
            .ThenByDescending(r => r.Id)
            .Select(r => r.SeriesId)
            .FirstOrDefaultAsync(ct);
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
