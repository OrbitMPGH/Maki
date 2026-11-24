using PuppeteerSharp;

namespace Maki.Sources.Common;

/// <summary>
/// Not every browser wait takes the caller's token (a response body read, an evaluate), so a
/// cancelled caller would otherwise keep the browser's shared gate for the rest of a walk nobody
/// wants any more. Cancelling closes the page, which fails most of those waits, and returns at once
/// even when it does not: a body read waits for a DevTools event that a closed page or a dead
/// Chromium may never send. The abandoned call fails on the closed page in the background.
/// </summary>
internal static class CancellableBrowserCall
{
    public static async Task<T> RunAsync<T>(Func<Task> closePage, Func<Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The call ends on this signal rather than on WaitAsync(ct): a token runs its newest callback
        // first, so WaitAsync's would end the call and dispose this registration before the page closed.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() =>
        {
            try
            {
                _ = closePage();
            }
            finally
            {
                cancelled.TrySetResult();
            }
        });
        var work = action();
        try
        {
            if (await Task.WhenAny(work, cancelled.Task) != work)
            {
                _ = work.ContinueWith(static t =>
                {
                    if (t.IsFaulted)
                    {
                        _ = t.Exception;
                    }
                    else if (t.IsCompletedSuccessfully && t.Result is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new OperationCanceledException(ct);
            }
            return await work;
        }
        catch (Exception ex) when ((ex is PuppeteerException or TimeoutException) && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
