using Microsoft.Playwright;

namespace Maki.Sources.Common;

/// <summary>
/// Playwright's navigation, response and timeout waits take their own timeouts and never see a
/// <see cref="CancellationToken"/>, so a cancelled caller would otherwise keep the browser's shared
/// gate for the rest of a walk nobody wants any more. Closing the page is what makes those waits
/// fail, and the failure that follows is reported as the cancellation it is.
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
        catch (PlaywrightException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
