using Mangarr.Core.Http;

namespace Mangarr.Core.Tests;

/// <summary>Records how often the download cooldown was awaited. Never actually delays.</summary>
internal sealed class FakeCooldown : IDownloadCooldown
{
    public int Waits;
    public TimeSpan Value { get; set; } = TimeSpan.Zero;

    public TimeSpan Remaining() => Value;

    public Task WaitAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref Waits);
        return Task.CompletedTask;
    }
}
