namespace Maki.Core;

/// <summary>
/// The one in-flight build of a cached artifact, shared by every caller waiting for it.
///
/// <para>
/// The build runs on no caller's token. A request that gives up (a search box aborted mid-keystroke)
/// only stops waiting; it neither throws away seconds of scanning nor leaves the next caller to
/// start the same build again from zero.
/// </para>
/// </summary>
public sealed class SharedBuild<T> where T : class
{
    private Task<T?>? _running;

    public bool IsRunning => Volatile.Read(ref _running) is not null;

    /// <summary>The running build, or <paramref name="build"/> started. See the other overload.</summary>
    public Task<T?> Join(Func<T?> build) => Join(build, out _);

    /// <summary>
    /// The running build, or <paramref name="build"/> started on the thread pool. Call under the
    /// owner's lock so two callers cannot both start one. The build publishes its own result, since
    /// nobody may be waiting by the time it finishes.
    /// </summary>
    /// <param name="started">
    /// False when this joined a build already under way, which may have read its input before the
    /// caller looked at it.
    /// </param>
    public Task<T?> Join(Func<T?> build, out bool started)
    {
        if (Volatile.Read(ref _running) is { } running)
        {
            started = false;
            return running;
        }

        started = true;
        var done = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _running, done.Task);
        _ = Task.Run(() =>
        {
            try
            {
                var result = build();
                Interlocked.CompareExchange(ref _running, null, done.Task);
                done.SetResult(result);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _running, null, done.Task);
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Waits out a running build however it ends. For an owner about to replace the file a build
    /// reads; call it under the same lock as <see cref="Join"/> so no new build can start meanwhile.
    /// </summary>
    public async Task DrainAsync()
    {
        if (Volatile.Read(ref _running) is not { } running)
        {
            return;
        }

        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The build's own callers see its failure; a swap only needs it to have stopped reading.
        }
    }
}
