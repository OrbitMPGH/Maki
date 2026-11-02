using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Maki.Metadata;

/// <summary>
/// A built in-memory index written to disk so a reload after an idle unload reads one file into the
/// arrays it ends up in instead of rebuilding from SQLite. Shared by the vector index and the
/// catalogue indexes.
///
/// <para>
/// A file is only ever read back under the exact key it was written with, and a key always carries
/// <see cref="BuildIdentity"/>, so a new release never reads an older layout. Anything that does
/// not check out (wrong magic, wrong key, short, a missing trailer) reads as no snapshot, and the
/// caller rebuilds over it. Written to a temporary name and moved into place, so a reader never
/// sees half a file.
/// </para>
/// </summary>
internal static class SnapshotFile
{
    private const ulong Trailer = 0x2E444E4549584449; // "IDXIEND."

    /// <summary>Changes whenever this assembly is rebuilt.</summary>
    public static readonly string BuildIdentity =
        typeof(SnapshotFile).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static void Save(string path, ulong magic, string key, Action<BinaryWriter> body)
    {
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(magic);
                writer.Write(key);
                body(writer);
                writer.Write(Trailer);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>What <paramref name="body"/> read, or null when there is no usable snapshot under <paramref name="key"/>.</summary>
    public static T? Load<T>(string path, ulong magic, string key, Func<BinaryReader, T?> body) where T : class
    {
        if (!BitConverter.IsLittleEndian || !File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 16 || reader.ReadUInt64() != magic || reader.ReadString() != key)
        {
            return null;
        }

        var value = body(reader);
        return value is not null && reader.ReadUInt64() == Trailer && stream.Position == stream.Length ? value : null;
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void WriteArray<T>(BinaryWriter writer, ReadOnlySpan<T> values) where T : unmanaged
    {
        writer.Write(values.Length);
        writer.Flush();
        writer.BaseStream.Write(MemoryMarshal.AsBytes(values));
    }

    public static T[] ReadArray<T>(BinaryReader reader) where T : unmanaged
    {
        var length = ReadCount(reader);
        if ((long)length * Unsafe.SizeOf<T>() > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new EndOfStreamException("The snapshot is shorter than its header says");
        }

        var values = new T[length];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    public static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        return count >= 0 ? count : throw new InvalidDataException("Negative length in a snapshot");
    }
}
