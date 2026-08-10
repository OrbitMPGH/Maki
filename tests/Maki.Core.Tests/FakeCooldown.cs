using Maki.Core.Http;

namespace Maki.Core.Tests;

/// <summary>Records how often the download cooldown was awaited. Never actually delays.</summary>
internal sealed class FakeCooldown : IDownloadCooldown
{
    public int Waits;
    public TimeSpan Value { get; set; } = TimeSpan.Zero;

    public TimeSpan Remaining(string sourceName) => Value;

    public Task WaitAsync(string sourceName, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Waits);
        return Task.CompletedTask;
    }
}
