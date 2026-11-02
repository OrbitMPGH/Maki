using Microsoft.IO;

namespace Maki.Core.Io;

/// <summary>
/// Memory streams for page-sized buffers, built from pooled 128 KB blocks.
///
/// <para>
/// A page served from an archive used to be copied into a <see cref="MemoryStream"/> sized to the
/// entry, which for nearly every page is over 85 KB and so a Large Object Heap allocation per page
/// view. The LOH is not compacted by default, so a reading session left the heap fragmented. Blocks
/// stay under the LOH threshold and go back to the pool when the stream is disposed, which every
/// caller does (an ASP.NET file result disposes its stream after writing it).
/// </para>
/// </summary>
public static class PooledStreams
{
    private static readonly RecyclableMemoryStreamManager Manager = new(new RecyclableMemoryStreamManager.Options
    {
        BlockSize = 128 * 1024,
        LargeBufferMultiple = 1024 * 1024,
        MaximumBufferSize = 64 * 1024 * 1024,
        MaximumSmallPoolFreeBytes = 16 * 1024 * 1024,
        MaximumLargePoolFreeBytes = 16 * 1024 * 1024,
        AggressiveBufferReturn = true,
    });

    /// <param name="tag">What the stream is for, shown by the manager's diagnostics.</param>
    /// <param name="capacity">Expected size, when known; reserves the blocks up front.</param>
    public static MemoryStream Get(string tag, long capacity = 0) =>
        capacity > 0 ? Manager.GetStream(tag, capacity) : Manager.GetStream(tag);
}
