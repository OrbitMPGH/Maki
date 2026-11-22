using Microsoft.IO;

namespace Maki.Core.Io;

/// <summary>
/// Memory streams for page-sized buffers, built from pooled 64 KB blocks.
///
/// <para>
/// A page served from an archive used to be copied into a <see cref="MemoryStream"/> sized to the
/// entry, which for nearly every page is over 85 KB and so a Large Object Heap allocation per page
/// view. The LOH is not compacted by default, so a reading session left the heap fragmented. Blocks
/// stay under the 85 KB LOH threshold and go back to the pool when the stream is disposed, which every
/// caller does (an ASP.NET file result disposes its stream after writing it).
/// </para>
/// </summary>
public static class PooledStreams
{
    private static readonly RecyclableMemoryStreamManager Manager = new(new RecyclableMemoryStreamManager.Options
    {
        BlockSize = 64 * 1024,
        LargeBufferMultiple = 1024 * 1024,
        MaximumBufferSize = 64 * 1024 * 1024,
        MaximumSmallPoolFreeBytes = 4 * 1024 * 1024,
        MaximumLargePoolFreeBytes = 4 * 1024 * 1024,
        AggressiveBufferReturn = true,
    });

    /// <param name="tag">What the stream is for, shown by the manager's diagnostics.</param>
    /// <param name="capacity">Expected size, when known; reserves the blocks up front.</param>
    public static MemoryStream Get(string tag, long capacity = 0) =>
        capacity > 0 ? Manager.GetStream(tag, capacity) : Manager.GetStream(tag);

    /// <summary>
    /// Reads the rest of <paramref name="stream"/> into one exact-size array. Seekable streams skip
    /// intermediate buffers altogether; unseekable ones buffer in pooled 64 KB blocks instead of a
    /// doubling <see cref="MemoryStream"/> on the LOH.
    /// </summary>
    public static async Task<byte[]> ReadAllBytesAsync(Stream stream, string tag, CancellationToken ct)
    {
        if (stream.CanSeek && stream.Length - stream.Position is var left && left >= 0 && left <= Array.MaxLength)
        {
            var exact = new byte[left];
            await stream.ReadExactlyAsync(exact, ct);
            return exact;
        }

        using var copy = Get(tag);
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    /// <summary>
    /// Reads <paramref name="stream"/> up to <paramref name="maxBytes"/> using pooled 64 KB blocks,
    /// returning null as soon as the stream exceeds the cap.
    /// </summary>
    public static async Task<byte[]?> ReadAllBytesCappedAsync(
        Stream stream, long maxBytes, string tag, CancellationToken ct, long? expectedLength = null)
    {
        if (expectedLength is > 0 and var declared && declared > maxBytes)
        {
            return null;
        }

        var capacity = expectedLength is > 0 ? Math.Min(expectedLength.Value, maxBytes) : 0;
        using var copy = Get(tag, capacity);
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (copy.Length + read > maxBytes)
                {
                    return null;
                }

                copy.Write(chunk, 0, read);
            }

            return copy.ToArray();
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
