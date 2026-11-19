using PuppeteerSharp;

namespace Maki.Sources.Common;

/// <summary>
/// Not every browser wait takes the caller's token (a response body read, an evaluate), so a
/// cancelled caller would otherwise keep the browser's shared gate for the rest of a walk nobody
/// wants any more. Closing the page is what makes those waits fail, and the failure that follows is
/// reported as the cancellation it is.
/// </summary>
internal static class CancellableBrowserCall
{
    public static async Task<T> RunAsync<T>(Func<Task> closePage, Func<Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(() => _ = closePage());
        try
        {
            return await action();
        }
        catch (Exception ex) when ((ex is PuppeteerException or TimeoutException) && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
