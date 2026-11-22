using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using Maki.Core.Io;
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
    private static readonly ConcurrentQueue<(SafeMemoryMappedViewHandle View, string Path, FileStamp Stamp, long Due)> Retired = new();
    private static readonly Timer Sweeper = new(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly ConcurrentDictionary<string, PathState> Paths = new(StringComparer.Ordinal);

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly SafeMemoryMappedViewHandle _view;
    private readonly string _path;
    private readonly FileStamp _stamp;
    private int _released;

    private SnapshotMapping(
        MemoryMappedFile file, MemoryMappedViewAccessor accessor, string path, FileStamp stamp, byte* pointer, long length)
    {
        _file = file;
        _accessor = accessor;
        _view = accessor.SafeMemoryMappedViewHandle;
        _path = path;
        _stamp = stamp;
        Pointer = pointer;
        Length = length;
    }

    public byte* Pointer { get; }

    public long Length { get; }

    /// <summary>The mapped file, or null when it does not exist or is empty.</summary>
    public static SnapshotMapping? Open(string path)
    {
        path = Path.GetFullPath(path);
        // Opens of a path and its last-retirement drop are serialized, so a drop never lands on a fresh mapping.
        var state = Paths.GetOrAdd(path, _ => new PathState());
        lock (state)
        {
            return OpenLocked(path, state);
        }
    }

    private static SnapshotMapping? OpenLocked(string path, PathState state)
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
        FileStamp stamp;
        try
        {
            stamp = new FileStamp(length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        }
        catch
        {
            stream.Dispose();
            throw;
        }

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
            state.Live++;
            return new SnapshotMapping(file, accessor, path, stamp, pointer + accessor.PointerOffset, length);
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
    public void Dispose() => Release(drop: false);

    /// <summary>Unmaps now as a swept retirement would, page cache drop included, for tests.</summary>
    internal bool ReleaseAsRetired() => Release(drop: true);

    private bool Release(bool drop)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return false;
        }

        GC.SuppressFinalize(this);
        Unmap(_view);
        var dropped = Forget(_path, drop ? _stamp : null);
        _accessor.Dispose();
        _file.Dispose();
        return dropped;
    }

    ~SnapshotMapping()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            Retired.Enqueue((_view, _path, _stamp, Environment.TickCount64 + (long)Grace.TotalMilliseconds));
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
            // A snapshot mapped twice promotes to active_file and stays on docker stats after the
            // unmap. Dropped only once no mapping of the path is left, so a reload is never evicted.
            Forget(next.Path, next.Stamp);
        }

        if (Retired.TryPeek(out var pending))
        {
            Sweeper.Change(TimeSpan.FromMilliseconds(Math.Max(1000, pending.Due - now)), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>How many retired views are still waiting out their grace, for tests.</summary>
    internal static int RetiredCount => Retired.Count;

    /// <summary>
    /// Counts one mapping of the path gone. With a stamp, drops the file's pages when it was the last
    /// mapping and the file at the path is still the one that was mapped; true when it dropped.
    /// </summary>
    private static bool Forget(string path, FileStamp? stamp)
    {
        if (!Paths.TryGetValue(path, out var state))
        {
            return false;
        }

        lock (state)
        {
            state.Live--;
            if (state.Live > 0 || stamp is not { } mapped || StampOf(path) != mapped)
            {
                return false;
            }

            PageCache.DropAfterScan(path);
            return true;
        }
    }

    private static FileStamp? StampOf(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new FileStamp(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct FileStamp(long Length, DateTime LastWriteUtc);

    private sealed class PathState
    {
        public int Live;
    }

    private static void Unmap(SafeMemoryMappedViewHandle view)
    {
        view.ReleasePointer();
        view.Dispose();
    }
}
