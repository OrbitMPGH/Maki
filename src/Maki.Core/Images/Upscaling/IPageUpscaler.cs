namespace Maki.Core.Images.Upscaling;

/// <summary>Runs one page through a server-side upscale model.</summary>
public interface IPageUpscaler
{
    /// <summary>
    /// Never throws for a bad page or a failing model: those come back as <see cref="UpscaleOutcome.Skipped"/>
    /// or <see cref="UpscaleOutcome.Failed"/>. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct);

    /// <summary>
    /// <see cref="UpscaleAsync"/> in two stages: this runs the decode and inference, and
    /// <see cref="PendingUpscale.FinishAsync"/> the rest, so a caller can start the next page while
    /// this one finishes. Same errors and cancellation as <see cref="UpscaleAsync"/>.
    /// </summary>
    async Task<PendingUpscale> StartAsync(byte[] page, string modelId, UpscaleRun run, CancellationToken ct) =>
        PendingUpscale.Done(await UpscaleAsync(page, modelId, ct));

    /// <summary>Drops model sessions nothing is using right now. The worker calls it when its queue drains.</summary>
    void ReleaseIdleSessions()
    {
    }
}

/// <summary>How the worker wants one page run.</summary>
public sealed record UpscaleRun
{
    public static readonly UpscaleRun Default = new();

    /// <summary>
    /// Nobody waits on the page (prepare-ahead): its tiles and colour pass take half the workers, so a
    /// reader's page beside it is not slowed, until <see cref="Widen"/> fires.
    /// </summary>
    public bool Narrow { get; init; }

    /// <summary>Cancelled when a reader starts waiting on a narrow page: the rest of it runs at full width.</summary>
    public CancellationToken Widen { get; init; }

    /// <summary>Whether the page should run narrow at this moment.</summary>
    public bool NarrowNow => Narrow && !Widen.IsCancellationRequested;

    /// <summary>The worker count a narrow page gets out of <paramref name="threads"/>.</summary>
    public static int NarrowWorkers(int threads) => Math.Max(1, threads / 2);
}

/// <summary>A page past inference whose colour pass and encode may still be left to do.</summary>
public sealed class PendingUpscale(Func<CancellationToken, Task<UpscaleResult>> finish)
{
    public static PendingUpscale Done(UpscaleResult result) => new(_ => Task.FromResult(result)) { Result = result };

    /// <summary>The result when nothing is left to finish (made by <see cref="Done"/>), else null.</summary>
    public UpscaleResult? Result { get; private init; }

    /// <summary>Call once. Never throws for a failing encode; throws when <paramref name="ct"/> is cancelled.</summary>
    public Task<UpscaleResult> FinishAsync(CancellationToken ct) => finish(ct);
}
