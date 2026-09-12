using Maki.Api.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// The gate exists to stop the startup schedule running several index builds at once, so the only
/// property that matters is that the second caller genuinely waits. Asserted directly rather than
/// through the jobs: the jobs download and scan gigabytes, and what is being pinned here is the
/// gate's own behaviour.
/// </summary>
public class ArtifactBuildGateTests
{
    private static ArtifactBuildGate Gate() => new(NullLogger<ArtifactBuildGate>.Instance);

    [Fact]
    public async Task SecondCallerWaitsForTheFirstToFinish()
    {
        var gate = Gate();
        var first = await gate.EnterAsync("first", CancellationToken.None);

        var second = gate.EnterAsync("second", CancellationToken.None);
        Assert.False(second.IsCompleted);

        first.Dispose();
        var lease = await second.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        lease.Dispose();
    }

    /// <summary>
    /// A job that disposes its lease and then unwinds through a catch could release twice, which
    /// would raise the semaphore's count and let two builds in at once from then on - the exact
    /// thing the gate exists to prevent, and silent when it happens.
    /// </summary>
    [Fact]
    public async Task DisposingTwiceDoesNotWidenTheGate()
    {
        var gate = Gate();
        var lease = await gate.EnterAsync("first", CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        var held = await gate.EnterAsync("second", CancellationToken.None);
        var third = gate.EnterAsync("third", CancellationToken.None);
        Assert.False(third.IsCompleted);

        held.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Dispose();
    }

    [Fact]
    public async Task CancellingAWaitLeavesTheGateUsable()
    {
        var gate = Gate();
        var held = await gate.EnterAsync("holder", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiting = gate.EnterAsync("cancelled", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        held.Dispose();
        var next = await gate.EnterAsync("next", CancellationToken.None);
        next.Dispose();
    }
}
