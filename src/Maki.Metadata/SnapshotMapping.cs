using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Maki.Metadata;

/// <summary>
/// A snapshot file mapped read-only into the address space, which <see cref="Column{T}"/> slices
/// point into. Opened with <see cref="FileShare.Delete"/>, so on Windows the file can still be
/// deleted while mapped (which is how <see cref="SnapshotFile.Save"/> replaces it); on Linux a
/// rename or unlink never touches a live mapping anyway.
///
/// <para>
/// Unmapping is the dangerous part. Every column holding this object keeps it alive, but a span
/// taken from a column does not, so the last index referencing a mapping can become unreachable
/// while a scan still reads a span of it. The finalizer therefore never unmaps: it retires the view
/// and a timer unmaps it <see cref="Grace"/> later, long after any synchronous span has gone.
/// </para>
/// </summary>
internal sealed unsafe class SnapshotMapping
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentQueue<(SafeMemoryMappedViewHandle View, long Due)> Retired = new();
    private static readonly Timer Sweeper = new(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly SafeMemoryMappedViewHandle _view;
    private int _released;

    private SnapshotMapping(MemoryMappedFile file, MemoryMappedViewAccessor accessor, byte* pointer, long length)
    {
        _file = file;
        _accessor = accessor;
        _view = accessor.SafeMemoryMappedViewHandle;
        Pointer = pointer;
        Length = length;
    }

    public byte* Pointer { get; }

    public long Length { get; }

    /// <summary>The mapped file, or null when it does not exist or is empty.</summary>
    public static SnapshotMapping? Open(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (stream.Length == 0)
        {
            stream.Dispose();
            return null;
        }

        var length = stream.Length;
        MemoryMappedFile file;
        try
        {
            file = MemoryMappedFile.CreateFromFile(
                stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        MemoryMappedViewAccessor? accessor = null;
        try
        {
            accessor = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            // Held until release: an outstanding acquire is what stops the handle's own finalizer
            // from unmapping the view before the grace period is up.
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            return new SnapshotMapping(file, accessor, pointer + accessor.PointerOffset, length);
        }
        catch
        {
            accessor?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>A stream over the mapped bytes, for the header and the parts that are parsed.</summary>
    public UnmanagedMemoryStream CreateStream() => new(Pointer, Length);

    /// <summary>
    /// Unmaps now. Only for a mapping no column has escaped from: a load that came to nothing.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            GC.SuppressFinalize(this);
            Unmap(_view);
            _accessor.Dispose();
            _file.Dispose();
        }
    }

    ~SnapshotMapping()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            Retired.Enqueue((_view, Environment.TickCount64 + (long)Grace.TotalMilliseconds));
            Sweeper.Change(Grace, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Unmaps every retired view whose grace is up, and re-arms for the rest.</summary>
    internal static void Sweep()
    {
        var now = Environment.TickCount64;
        while (Retired.TryPeek(out var next) && next.Due <= now && Retired.TryDequeue(out next))
        {
            Unmap(next.View);
        }

        if (Retired.TryPeek(out var pending))
        {
            Sweeper.Change(TimeSpan.FromMilliseconds(Math.Max(1000, pending.Due - now)), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>How many retired views are still waiting out their grace, for tests.</summary>
    internal static int RetiredCount => Retired.Count;

    private static void Unmap(SafeMemoryMappedViewHandle view)
    {
        view.ReleasePointer();
        view.Dispose();
    }
}
