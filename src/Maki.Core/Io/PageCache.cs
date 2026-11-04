using System.Buffers;
using System.Runtime.InteropServices;

namespace Maki.Core.Io;

/// <summary>
/// Asks the kernel to forget the file pages a one-shot scan pulled in.
///
/// <para>
/// Reading a file leaves its contents in the page cache, which is normally free and useful. It
/// stops being either when the read is a single linear pass over something enormous that nothing
/// will touch again: a health verify walking every archive in a library, or an index build scanning
/// a three-gigabyte dump. Measured on a NAS six minutes into a start, the container's page cache
/// was 744 MB against 405 MB of anonymous memory for the process itself.
/// </para>
///
/// <para>
/// Those pages are clean and reclaimable, so this is not a leak and the kernel would drop them the
/// moment anything else wanted the RAM. It matters anyway for two reasons: a container's memory as
/// every dashboard reports it counts them, so an operator cannot tell this apart from a process
/// that is genuinely using a gigabyte; and on an 8 GB NAS, cache this container will never read
/// again is evicting cache other containers would have.
/// </para>
///
/// <para>
/// Only the scans that read a file end to end call this. A point query's pages are worth keeping,
/// and dropping the whole file after one would make the next query read from the disk again.
/// <c>MAKI_DROP_SCAN_CACHE=0</c> turns it off for an install with RAM to spare.
/// </para>
/// </summary>
public static class PageCache
{
    public const string DisableVariable = "MAKI_DROP_SCAN_CACHE";

    /// <summary>POSIX_FADV_DONTNEED. Linux drops the file's clean, unmapped pages.</summary>
    private const int DontNeed = 4;

    [DllImport("libc", EntryPoint = "posix_fadvise")]
    private static extern int PosixFadvise(int fd, long offset, long length, int advice);

    private static readonly bool Enabled =
        OperatingSystem.IsLinux() &&
        !string.Equals(Environment.GetEnvironmentVariable(DisableVariable), "0", StringComparison.Ordinal);

    /// <summary>How much of a <see cref="CopyDroppingWrittenAsync"/> write may sit in the cache.</summary>
    public const long WriteWindowBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> and keeps the written
    /// file out of the page cache as it goes: every <paramref name="window"/> bytes it syncs what it
    /// wrote and drops that range. Dirty pages cannot be dropped, so the sync is what makes the drop
    /// work. Without it a 3.5 GB dump install charges all 3.5 GB to the container until the job ends.
    /// A plain copy off Linux or when disabled.
    /// </summary>
    public static async Task CopyDroppingWrittenAsync(
        Stream source, FileStream destination, CancellationToken ct, long window = WriteWindowBytes)
    {
        if (!Enabled)
        {
            await source.CopyToAsync(destination, ct);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long written = 0, dropped = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (written - dropped >= window)
                {
                    destination.Flush(flushToDisk: true);
                    Advise(destination, dropped, written - dropped);
                    dropped = written;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Advise(FileStream file, long offset, long length)
    {
        try
        {
            PosixFadvise((int)file.SafeFileHandle.DangerousGetHandle(), offset, length, DontNeed);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Drops <paramref name="path"/> from the page cache. A no-op off Linux, when disabled, or when
    /// the file cannot be opened - this is an optimisation, and nothing about the caller's work
    /// changes if the kernel keeps the pages after all.
    /// </summary>
    public static void DropAfterScan(string path)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // Read-only, and sharing everything: SQLite may well have the same file open, and the
            // advice applies to the file's pages rather than to this descriptor.
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // A length of zero means "to the end of the file".
            PosixFadvise((int)handle.DangerousGetHandle(), 0, 0, DontNeed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            // Gone, unreadable, or a libc without the call. Keeping the pages is the old behaviour.
        }
    }
}
