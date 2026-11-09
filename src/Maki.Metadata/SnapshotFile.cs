using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Maki.Metadata;

/// <summary>
/// A built in-memory index written to disk so a reload after an idle unload maps one file instead
/// of rebuilding from SQLite. Shared by the vector index, the catalogue indexes and the co-read graph.
///
/// <para>
/// A file is only ever read back under the exact key it was written with, and a key always carries
/// <see cref="BuildIdentity"/>, so a new release never reads an older layout. Anything that does
/// not check out (wrong magic, wrong key, short, a missing trailer) reads as no snapshot, and the
/// caller rebuilds over it. Written to a temporary name and moved into place, so a reader never
/// sees half a file.
/// </para>
///
/// <para>
/// <see cref="Load"/> maps the file and every array comes back as a <see cref="Column{T}"/> over the
/// mapping, so the values are never copied onto the heap. Each array starts on a
/// <see cref="Alignment"/>-byte boundary of the file so those columns are aligned in memory too.
/// The file must never be written in place while mapped (on Linux that is a SIGBUS); it is only
/// ever replaced or deleted.
/// </para>
/// </summary>
internal static class SnapshotFile
{
    private const ulong Trailer = 0x2E444E4549584449; // "IDXIEND."
    private const int Alignment = 16;

    /// <summary>Changes whenever this assembly is rebuilt.</summary>
    public static readonly string BuildIdentity =
        typeof(SnapshotFile).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static void Save(string path, ulong magic, string key, Action<BinaryWriter> body)
    {
        // Unique per save: two saves racing on one name would delete each other's file.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        DeleteStaleTemps(path);
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

            try
            {
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       && ex is not FileNotFoundException && File.Exists(temp) && File.Exists(path))
            {
                // Windows refuses to move over a file that is mapped, but lets it be deleted because
                // every mapping opens it with FileShare.Delete; the old mapping keeps reading.
                File.Delete(path);
                File.Move(temp, path);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// <c>MAKI_SNAPSHOT_MMAP=0</c> reads snapshots onto the heap instead of mapping them, for a
    /// filesystem where a mapping misbehaves. A mapping that fails to open falls back the same way.
    /// </summary>
    private static readonly bool MappingEnabled = Environment.GetEnvironmentVariable("MAKI_SNAPSHOT_MMAP") != "0";

    /// <summary>What <paramref name="body"/> read, or null when there is no usable snapshot under <paramref name="key"/>.</summary>
    public static T? Load<T>(string path, ulong magic, string key, Func<BinaryReader, T?> body) where T : class
    {
        if (!BitConverter.IsLittleEndian || !File.Exists(path))
        {
            return null;
        }

        SnapshotMapping? mapping = null;
        if (MappingEnabled)
        {
            try
            {
                mapping = SnapshotMapping.Open(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        return mapping is null ? LoadCopy(path, magic, key, body) : LoadMapped(mapping, magic, key, body);
    }

    private static T? LoadCopy<T>(string path, ulong magic, string key, Func<BinaryReader, T?> body) where T : class
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 16 || reader.ReadUInt64() != magic || reader.ReadString() != key)
        {
            return null;
        }

        var value = body(reader);
        return value is not null && reader.ReadUInt64() == Trailer && stream.Position == stream.Length ? value : null;
    }

    private static T? LoadMapped<T>(SnapshotMapping mapping, ulong magic, string key, Func<BinaryReader, T?> body) where T : class
    {
        var keep = false;
        try
        {
            using var stream = mapping.CreateStream();
            using var reader = new SnapshotReader(stream, mapping);
            if (stream.Length < 16 || reader.ReadUInt64() != magic || reader.ReadString() != key)
            {
                return null;
            }

            var value = body(reader);
            if (value is not null && reader.ReadUInt64() == Trailer && stream.Position == stream.Length)
            {
                keep = reader.Mapped;
                return value;
            }

            return null;
        }
        finally
        {
            if (!keep)
            {
                mapping.Dispose();
            }
        }
    }

    /// <summary>Temp files a crashed save left behind. An hour old is well past any live save.</summary>
    private static void DeleteStaleTemps(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var stale in Directory.EnumerateFiles(folder, Path.GetFileName(path) + ".*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(stale) < cutoff)
                {
                    TryDelete(stale);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
        var stream = writer.BaseStream;
        var padding = Padding(stream.Position);
        for (var i = 0; i < padding; i++)
        {
            stream.WriteByte(0);
        }

        stream.Write(MemoryMarshal.AsBytes(values));
    }

    /// <summary>
    /// The next array: a slice of the mapping when <paramref name="reader"/> came from
    /// <see cref="Load"/>, a heap copy for any other stream.
    /// </summary>
    public static Column<T> ReadArray<T>(BinaryReader reader) where T : unmanaged
    {
        var length = ReadCount(reader);
        var stream = reader.BaseStream;
        var start = stream.Position + Padding(stream.Position);
        var bytes = (long)length * Unsafe.SizeOf<T>();
        if (start + bytes > stream.Length)
        {
            throw new EndOfStreamException("The snapshot is shorter than its header says");
        }

        stream.Position = start;
        if (length > 0 && reader is SnapshotReader mapped)
        {
            stream.Position = start + bytes;
            return mapped.Column<T>(start, length);
        }

        var values = new T[length];
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    public static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        return count >= 0 ? count : throw new InvalidDataException("Negative length in a snapshot");
    }

    private static int Padding(long position) => (int)((Alignment - position % Alignment) % Alignment);

    private sealed unsafe class SnapshotReader(UnmanagedMemoryStream stream, SnapshotMapping mapping)
        : BinaryReader(stream, Encoding.UTF8, leaveOpen: true)
    {
        public bool Mapped { get; private set; }

        public Column<T> Column<T>(long offset, int length) where T : unmanaged
        {
            Mapped = true;
            return new Column<T>(mapping, (T*)(mapping.Pointer + offset), length);
        }
    }
}
