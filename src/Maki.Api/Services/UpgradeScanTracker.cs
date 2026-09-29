using System.Collections.Concurrent;

namespace Maki.Api.Services;

/// <param name="State">"queued", "running", "done", "busy" (another scan held the gate, nothing ran) or "failed".</param>
/// <param name="ChaptersChecked">Filled once done.</param>
/// <param name="Skipped">Reason code to count, once done.</param>
public sealed record SeriesScanStatus(
    string State, DateTime QueuedAtUtc, DateTime? FinishedAtUtc, int ChaptersChecked, int Probed, int Queued,
    IReadOnlyDictionary<string, int>? Skipped = null);

/// <summary>
/// What became of the per-series upgrade scans asked for since startup, so the series page can show
/// one running and say how it went. The scan itself runs as a Quartz job and returns nothing to the
/// request that queued it.
/// </summary>
public sealed class UpgradeScanTracker
{
    private readonly ConcurrentDictionary<int, SeriesScanStatus> _series = new();

    public SeriesScanStatus? Status(int seriesId) => _series.GetValueOrDefault(seriesId);

    public void Queued(int seriesId) =>
        _series[seriesId] = new SeriesScanStatus("queued", DateTime.UtcNow, null, 0, 0, 0);

    public void Running(int seriesId) => Update(seriesId, s => s with { State = "running" });

    public void Finished(int seriesId, UpgradeScanResult result) => Update(seriesId, s => s with
    {
        State = "done",
        FinishedAtUtc = DateTime.UtcNow,
        ChaptersChecked = result.ChaptersChecked,
        Probed = result.CandidatesProbed,
        Queued = result.Enqueued,
        Skipped = result.Skipped
    });

    public void Ended(int seriesId, string state) =>
        Update(seriesId, s => s with { State = state, FinishedAtUtc = DateTime.UtcNow });

    private void Update(int seriesId, Func<SeriesScanStatus, SeriesScanStatus> change) =>
        _series.AddOrUpdate(seriesId,
            _ => change(new SeriesScanStatus("queued", DateTime.UtcNow, null, 0, 0, 0)),
            (_, current) => change(current));
}
