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
        using var registration = ct.Register(() => _ = closePage());
        try
        {
            return await action().WaitAsync(ct);
        }
        catch (Exception ex) when ((ex is PuppeteerException or TimeoutException) && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
