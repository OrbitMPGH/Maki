using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// Snapshots are read in place through a mapping, and a span taken from a mapped column does not
/// keep the mapping alive, so an unreachable mapping must wait out a grace period before it unmaps.
/// </summary>
public class SnapshotMappingTests : IDisposable
{
    private const ulong Magic = 0x5453455450414D4D;

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "maki-map-" + Guid.NewGuid().ToString("N"))).FullName;

    private string PathOf(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Holder(Column<int> Values, Column<long> Wide);

    private static void Write(string path, string key, int[] values, long[] wide) =>
        SnapshotFile.Save(path, Magic, key, writer =>
        {
            writer.Write((byte)7);
            SnapshotFile.WriteArray<int>(writer, values);
            writer.Write("between");
            SnapshotFile.WriteArray<long>(writer, wide);
        });

    private static Holder? Read(string path, string key) =>
        SnapshotFile.Load(path, Magic, key, reader =>
        {
            reader.ReadByte();
            var values = SnapshotFile.ReadArray<int>(reader);
            return reader.ReadString() == "between" ? new Holder(values, SnapshotFile.ReadArray<long>(reader)) : null;
        });

    [Fact]
    public void Arrays_come_back_mapped_aligned_and_equal()
    {
        var path = PathOf("a.snap");
        Write(path, "k", [1, 2, 3, 5, 8], [long.MaxValue, -1]);

        var holder = Read(path, "k")!;

        Assert.True(holder.Values.IsMapped);
        Assert.Equal([1, 2, 3, 5, 8], holder.Values.Span.ToArray());
        Assert.Equal([long.MaxValue, -1], holder.Wide.Span.ToArray());
        var address = Unsafe.ByteOffset(ref Unsafe.NullRef<long>(), ref MemoryMarshal.GetReference(holder.Wide.Span));
        Assert.Equal(0, (long)address % 8);
    }

    [Fact]
    public void A_wrong_key_reads_nothing()
    {
        var path = PathOf("b.snap");
        Write(path, "k", [1], [2]);

        Assert.Null(Read(path, "other"));
    }

    [Fact]
    public void A_save_replaces_a_mapped_file_and_the_old_mapping_keeps_its_values()
    {
        var path = PathOf("c.snap");
        Write(path, "k", [1, 2, 3], [4]);
        var old = Read(path, "k")!;

        Write(path, "k", [9, 9], [9]);
        var fresh = Read(path, "k")!;

        Assert.Equal([1, 2, 3], old.Values.Span.ToArray());
        Assert.Equal([9, 9], fresh.Values.Span.ToArray());
    }

    [Fact]
    public void An_unreachable_mapping_is_retired_rather_than_unmapped()
    {
        var path = PathOf("d.snap");
        Write(path, "k", [1, 2, 3], [4]);
        var before = SnapshotMapping.RetiredCount;

        var orphaned = SpanOfDropped(path);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SnapshotMapping.Sweep();

        // Another test's mapping retiring at the same moment would also raise the count; the span
        // still reading is what pins this one.
        Assert.True(SnapshotMapping.RetiredCount > before);
        Assert.Equal([1, 2, 3], orphaned.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReadOnlySpan<int> SpanOfDropped(string path)
    {
        var column = Read(path, "k")!.Values;
        Assert.True(column.IsMapped);
        return column.Span;
    }

    [Fact]
    public void A_heap_reader_still_gets_arrays()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            SnapshotFile.WriteArray<int>(writer, [4, 5]);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        reader.ReadByte();
        var column = SnapshotFile.ReadArray<int>(reader);

        Assert.False(column.IsMapped);
        Assert.Equal([4, 5], column.Span.ToArray());
    }
}
