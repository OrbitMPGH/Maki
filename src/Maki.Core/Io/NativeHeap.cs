using System.Runtime.InteropServices;

namespace Maki.Core.Io;

/// <summary>
/// Hands glibc's free heap back to the kernel after something large has been released.
///
/// <para>
/// glibc only shrinks an arena from its top, so memory freed below a live allocation stays mapped
/// and counted as the process's own. Unloading the ONNX embedder, the upscaler's models or the
/// discovery artifacts, and every burst of libvips work, frees a lot of native memory in exactly
/// that shape. Measured in a container ten minutes after search and Discover had unloaded, a single
/// external <c>malloc_trim(0)</c> gave back 100 to 165 MB in three runs.
/// </para>
///
/// <para>
/// Linux only, and a no-op where the libc has no such call (musl).
/// </para>
/// </summary>
public static class NativeHeap
{
    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    [DllImport("libc", EntryPoint = "mallinfo2")]
    private static extern MallInfo2 MallInfo();

    private static int _trims;

    /// <summary>How many trims have run since the process started.</summary>
    public static int Trims => Volatile.Read(ref _trims);

    /// <summary>
    /// Releases free pages from every arena, not just the top of each. Takes the arena locks for a
    /// few milliseconds, so call it where something has just been released, never per request.
    /// </summary>
    public static void Trim()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            MallocTrim(0);
            Interlocked.Increment(ref _trims);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// glibc's own accounting, summed over every arena: what is in use, what is free but still
    /// mapped (the part a trim can return), and what sits in separate mappings. Null off Linux or on
    /// a glibc older than 2.33.
    /// </summary>
    public static NativeHeapInfo? Read()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var info = MallInfo();
            return new NativeHeapInfo(
                ArenaBytes: (long)info.Arena,
                InUseBytes: (long)info.Uordblks,
                FreeBytes: (long)info.Fordblks,
                MappedBytes: (long)info.Hblkhd,
                TrimmableBytes: (long)info.Keepcost);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MallInfo2
    {
        public nuint Arena;
        public nuint Ordblks;
        public nuint Smblks;
        public nuint Hblks;
        public nuint Hblkhd;
        public nuint Usmblks;
        public nuint Fsmblks;
        public nuint Uordblks;
        public nuint Fordblks;
        public nuint Keepcost;
    }
}

/// <param name="TrimmableBytes">Free space at the very top of the main arena, which glibc returns on its own once it passes the trim threshold.</param>
public sealed record NativeHeapInfo(
    long ArenaBytes,
    long InUseBytes,
    long FreeBytes,
    long MappedBytes,
    long TrimmableBytes);
