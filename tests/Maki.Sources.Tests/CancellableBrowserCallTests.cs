using Maki.Sources.Common;
using Microsoft.Playwright;

namespace Maki.Sources.Tests;

public class CancellableBrowserCallTests
{
    [Fact]
    public async Task Cancelling_closes_the_page_and_surfaces_a_cancellation()
    {
        var stuck = new TaskCompletionSource<int>();
        var closed = 0;
        using var cts = new CancellationTokenSource();

        var call = CancellableBrowserCall.RunAsync(
            () =>
            {
                closed++;
                stuck.SetException(new PlaywrightException("Target page, context or browser has been closed"));
                return Task.CompletedTask;
            },
            () => stuck.Task,
            cts.Token);

        Assert.False(call.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task A_playwright_failure_without_cancellation_is_left_alone()
    {
        await Assert.ThrowsAsync<PlaywrightException>(() => CancellableBrowserCall.RunAsync<int>(
            () => Task.CompletedTask,
            () => throw new PlaywrightException("boom"),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_token_cancelled_up_front_never_starts_the_action()
    {
        var started = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancellableBrowserCall.RunAsync(
            () => Task.CompletedTask,
            () =>
            {
                started = true;
                return Task.FromResult(0);
            },
            new CancellationToken(canceled: true)));

        Assert.False(started);
    }

    [Fact]
    public async Task A_finished_call_returns_its_result()
    {
        var result = await CancellableBrowserCall.RunAsync(
            () => Task.CompletedTask, () => Task.FromResult(7), CancellationToken.None);

        Assert.Equal(7, result);
    }
}
