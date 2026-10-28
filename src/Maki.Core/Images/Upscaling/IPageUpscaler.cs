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
}
