using System.Runtime.CompilerServices;

namespace Maki.Metadata;

/// <summary>
/// A read-only run of values that is either an ordinary array or a slice of a memory-mapped
/// snapshot (<see cref="SnapshotMapping"/>). The index types hold their columns as this so a reload
/// reads a snapshot in place: the values stay in the file's pages, which the OS can drop and refault
/// under pressure, instead of being copied onto the GC heap. Converts implicitly from an array, so
/// builders and tests keep passing arrays.
///
/// <para>
/// A mapped column holds its mapping, so the mapping lives as long as any index holding the
/// column. A span taken from it does not, and neither does a column copied into a local, since the
/// JIT may keep only the pointer and length live; <see cref="SnapshotMapping"/> delays the unmap for
/// that. Callers keep the owning index referenced for the length of a scan, which they all do.
/// </para>
/// </summary>
[CollectionBuilder(typeof(Column), nameof(Column.Create))]
public readonly unsafe struct Column<T> where T : unmanaged
{
    private readonly T[]? _array;
    private readonly SnapshotMapping? _mapping;
    private readonly T* _pointer;
    private readonly int _length;

    public Column(T[] array)
    {
        _array = array;
        _length = array.Length;
    }

    internal Column(SnapshotMapping mapping, T* pointer, int length)
    {
        _mapping = mapping;
        _pointer = pointer;
        _length = length;
    }

    public int Length => _length;

    /// <summary>True when the values are read from a mapped snapshot rather than the heap.</summary>
    public bool IsMapped => _mapping is not null;

    public ReadOnlySpan<T> Span => _array is { } array ? array : new ReadOnlySpan<T>(_pointer, _length);

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_array is { } array)
            {
                return array[index];
            }

            if ((uint)index >= (uint)_length)
            {
                throw new IndexOutOfRangeException();
            }

            return _pointer[index];
        }
    }

    public ReadOnlySpan<T> AsSpan() => Span;

    public ReadOnlySpan<T> AsSpan(int start) => Span[start..];

    public ReadOnlySpan<T> AsSpan(int start, int length) => Span.Slice(start, length);

    public ReadOnlySpan<T> Slice(int start, int length) => Span.Slice(start, length);

    public ReadOnlySpan<T>.Enumerator GetEnumerator() => Span.GetEnumerator();

    /// <summary>A null array becomes an empty column.</summary>
    public static implicit operator Column<T>(T[]? array) => array is null ? default : new(array);
}

/// <summary>Lets a collection expression initialize a <see cref="Column{T}"/>.</summary>
public static class Column
{
    public static Column<T> Create<T>(ReadOnlySpan<T> values) where T : unmanaged => values.ToArray();
}
