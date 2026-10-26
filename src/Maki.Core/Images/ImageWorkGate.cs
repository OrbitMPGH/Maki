using Maki.Core.Io;

namespace Maki.Core.Images;

/// <summary>
/// Process-wide ceiling on how many images are being decoded at once.
///
/// <para>
/// A decoded page is uncompressed pixels, not the file on disk: RGBA makes a 2000×3000 scan
/// ~24 MB, and a descramble holds two of them. Nothing used to bound how many of those existed together, because the limits that do exist are about something
/// else — <c>PageDownloader</c> caps pages per chapter, the download worker caps chapters, the
/// reader's thumbnail endpoint caps nothing at all — and they multiply rather than compose. Raising
/// the chapter-concurrency setting therefore bought a proportional rise in peak RSS that no
/// image-facing code had asked for.
/// </para>
///
/// <para>
/// Static rather than injected because it models a machine limit, not a policy: two callers in
/// different projects have to queue behind the same permits or the ceiling is not one. Gated work
/// must never gate again inside itself — the permits are not reentrant, and every current caller is
/// a leaf.
/// </para>
/// </summary>
public static class ImageWorkGate
{
    /// <summary>
    /// Half the cores, floored at 2 so a single-core container still makes progress and capped at 4
    /// because past that the limit is memory rather than CPU: four concurrent decodes of a large
    /// page is already ~100 MB of live pixels.
    /// </summary>
    private static readonly SemaphoreSlim Permits =
        new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    /// <summary>
    /// How long the gate has to stay empty before the decoders' freed buffers go back to the
    /// kernel. Long enough that a reader paging through a chapter or a scan between files never
    /// pays for it, short enough that a finished burst does not leave its heap behind for hours.
    /// </summary>
    private static readonly TimeSpan TrimAfter = TimeSpan.FromMinutes(2);

    private static int _active;

    private static readonly Timer IdleTrim = new(_ =>
    {
        if (Volatile.Read(ref _active) == 0)
        {
            NativeHeap.Trim();
        }
    });

    public static async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            Exit();
        }
    }

    public static async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await work();
        }
        finally
        {
            Exit();
        }
    }

    private static async Task EnterAsync(CancellationToken ct)
    {
        await Permits.WaitAsync(ct);
        Interlocked.Increment(ref _active);
    }

    private static void Exit()
    {
        Permits.Release();
        if (Interlocked.Decrement(ref _active) == 0)
        {
            // Re-armed by every last release, so it fires once the burst is over, not during it.
            IdleTrim.Change(TrimAfter, Timeout.InfiniteTimeSpan);
        }
    }
}
