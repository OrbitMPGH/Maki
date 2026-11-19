using Maki.Sources.Common;
using PuppeteerSharp;

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
                stuck.SetException(new PuppeteerException("Target page, context or browser has been closed"));
                return Task.CompletedTask;
            },
            () => stuck.Task,
            cts.Token);

        Assert.False(call.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, closed);
    }

    /// <summary>A body read waits on a DevTools event that a closed page or a dead Chromium may never
    /// send, so closing the page is not enough to end it; the cancellation has to.</summary>
    [Fact]
    public async Task Cancelling_returns_even_when_closing_the_page_does_not_end_the_wait()
    {
        var never = new TaskCompletionSource<int>();
        using var cts = new CancellationTokenSource();

        var call = CancellableBrowserCall.RunAsync(() => Task.CompletedTask, () => never.Task, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_body_that_never_arrives_times_out()
    {
        var response = Browser.Fake<IResponse>.Create((name, _) =>
            name == nameof(IResponse.TextAsync) ? new TaskCompletionSource<string>().Task : null).Instance;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            BrowserSupport.ReadTextAsync(response, TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    [Fact]
    public async Task A_body_read_ends_with_its_caller()
    {
        var response = Browser.Fake<IResponse>.Create((name, _) =>
            name == nameof(IResponse.TextAsync) ? new TaskCompletionSource<string>().Task : null).Instance;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrowserSupport.ReadTextAsync(response, TimeSpan.FromMinutes(1), cts.Token));
    }

    [Fact]
    public async Task A_browser_failure_without_cancellation_is_left_alone()
    {
        await Assert.ThrowsAsync<PuppeteerException>(() => CancellableBrowserCall.RunAsync<int>(
            () => Task.CompletedTask,
            () => throw new PuppeteerException("boom"),
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
