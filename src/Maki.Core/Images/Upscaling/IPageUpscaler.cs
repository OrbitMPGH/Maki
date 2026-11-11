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
    async Task<PendingUpscale> StartAsync(byte[] page, string modelId, CancellationToken ct) =>
        PendingUpscale.Done(await UpscaleAsync(page, modelId, ct));
}

/// <summary>A page past inference whose colour pass and encode may still be left to do.</summary>
public sealed class PendingUpscale(Func<CancellationToken, Task<UpscaleResult>> finish)
{
    public static PendingUpscale Done(UpscaleResult result) => new(_ => Task.FromResult(result));

    /// <summary>Call once. Never throws for a failing encode; throws when <paramref name="ct"/> is cancelled.</summary>
    public Task<UpscaleResult> FinishAsync(CancellationToken ct) => finish(ct);
}
