using static Maki.Metadata.SnapshotFile;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Everything a <see cref="VectorIndex"/> is built from except what is cheap to read live (the tag
/// vocabulary, the behavioural layer, the franchise components), as <see cref="VectorIndexCache"/>
/// either scans it or reads it back from a snapshot.
/// </summary>
internal sealed record VectorIndexParts(
    Column<long> Ids,
    Column<byte> Data,
    Column<float> Scales,
    int Dimensions,
    VectorIndexColumns Columns,
    Dictionary<string, byte> Types,
    Dictionary<string, byte> Statuses,
    Dictionary<string, int> Genres,
    string[] AuthorsById,
    Dictionary<string, byte> ContentRatings,
    TasteLayer? Taste = null);

/// <summary>
/// The built search vector index on disk (<see cref="SnapshotFile"/>), so a reload after an idle
/// unload skips joining ~128k vector rows to the dump and parsing their JSON. The key
/// <see cref="VectorIndexCache"/> writes it under covers the dump file, the vector database's
/// content version, the model and this build.
/// </summary>
internal static class VectorIndexSnapshot
{
    private const ulong Magic = 0x3158444E4B49414D; // "MAKINDX1"

    public static void Write(string path, string key, VectorIndexParts parts) => Save(path, Magic, key, writer =>
    {
        var rows = parts.Ids.Length;
        var columns = parts.Columns;

        writer.Write(rows);
        writer.Write(parts.Dimensions);
        WriteArray(writer, parts.Ids.AsSpan());
        WriteArray(writer, parts.Data.AsSpan(0, rows * EmbeddingMath.PackedStride(parts.Dimensions)));
        WriteArray(writer, parts.Scales.AsSpan());
        WriteArray(writer, columns.Years.AsSpan());
        WriteArray(writer, columns.Ratings.AsSpan());
        WriteArray(writer, columns.Chapters.AsSpan());
        WriteArray(writer, columns.Types.AsSpan());
        WriteArray(writer, columns.Statuses.AsSpan());
        Jagged(writer, columns.Genres);
        Jagged(writer, columns.Authors);
        Jagged(writer, columns.Artists);
        WriteArray(writer, columns.Popularity.AsSpan());
        WriteArray(writer, columns.Tags.Data.AsSpan());
        WriteArray(writer, columns.Tags.Offsets.AsSpan());
        WriteArray(writer, columns.ContentRatings.AsSpan());
        WriteArray(writer, columns.StartDays.Span);

        Vocabulary(writer, parts.Types, (w, v) => w.Write(v));
        Vocabulary(writer, parts.Statuses, (w, v) => w.Write(v));
        Vocabulary(writer, parts.Genres, (w, v) => w.Write(v));
        writer.Write(parts.AuthorsById.Length);
        foreach (var name in parts.AuthorsById)
        {
            writer.Write(name);
        }

        Vocabulary(writer, parts.ContentRatings, (w, v) => w.Write(v));

        writer.Write(parts.Taste is not null);
        if (parts.Taste is { } taste)
        {
            writer.Write(taste.Dimensions);
            WriteArray(writer, taste.Slots.Span);
            WriteArray(writer, taste.Data.Span);
            WriteArray(writer, taste.Scales.Span);
        }
    });

    /// <summary>The parts stored under <paramref name="key"/>, or null when there are none.</summary>
    public static VectorIndexParts? TryRead(string path, string key) => Load(path, Magic, key, reader =>
    {
        var rows = reader.ReadInt32();
        var dimensions = reader.ReadInt32();
        var ids = ReadArray<long>(reader);
        var data = ReadArray<byte>(reader);
        var scales = ReadArray<float>(reader);
        var years = ReadArray<int>(reader);
        var ratings = ReadArray<float>(reader);
        var chapters = ReadArray<int>(reader);
        var types = ReadArray<byte>(reader);
        var statuses = ReadArray<byte>(reader);
        var genres = Jagged(reader);
        var authors = Jagged(reader);
        var artists = Jagged(reader);
        var popularity = ReadArray<int>(reader);
        var tags = new PackedTags(ReadArray<byte>(reader), ReadArray<int>(reader));
        var contentRatings = ReadArray<byte>(reader);
        var startDays = ReadArray<int>(reader);

        var typeIds = Vocabulary(reader, r => r.ReadByte());
        var statusIds = Vocabulary(reader, r => r.ReadByte());
        var genreIds = Vocabulary(reader, r => r.ReadInt32());
        var authorsById = new string[ReadCount(reader)];
        for (var i = 0; i < authorsById.Length; i++)
        {
            authorsById[i] = reader.ReadString();
        }

        var contentRatingIds = Vocabulary(reader, r => r.ReadByte());
        TasteLayer? taste = null;
        if (reader.ReadBoolean())
        {
            var tasteDimensions = reader.ReadInt32();
            var slots = ReadArray<int>(reader);
            var tasteData = ReadArray<sbyte>(reader);
            var tasteScales = ReadArray<float>(reader);
            if (tasteDimensions <= 0 || slots.Length != rows || tasteData.Length != (long)tasteScales.Length * tasteDimensions)
            {
                return null;
            }

            taste = new TasteLayer(slots, tasteData, tasteScales, tasteDimensions);
        }

        if (ids.Length != rows || scales.Length != rows || years.Length != rows || ratings.Length != rows
            || chapters.Length != rows || types.Length != rows || statuses.Length != rows
            || popularity.Length != rows || contentRatings.Length != rows
            || data.Length != (long)rows * EmbeddingMath.PackedStride(dimensions)
            || genres.Count != rows || authors.Count != rows || artists.Count != rows || tags.Count != rows
            || (startDays.Length != 0 && startDays.Length != rows))
        {
            return null;
        }

        return new VectorIndexParts(
            ids, data, scales, dimensions,
            new VectorIndexColumns(
                years, ratings, chapters, types, statuses, genres, authors, artists, popularity, tags,
                contentRatings, [], startDays),
            typeIds, statusIds, genreIds, authorsById, contentRatingIds, taste);
    });

    private static void Jagged(BinaryWriter writer, JaggedInts column)
    {
        WriteArray(writer, column.Values.AsSpan());
        WriteArray(writer, column.Offsets.AsSpan());
    }

    private static JaggedInts Jagged(BinaryReader reader) =>
        JaggedInts.FromParts(ReadArray<int>(reader), ReadArray<int>(reader));

    /// <summary>In the dictionary's own order, so a reloaded vocabulary enumerates as the built one did.</summary>
    private static void Vocabulary<T>(BinaryWriter writer, Dictionary<string, T> vocabulary, Action<BinaryWriter, T> write)
    {
        writer.Write(vocabulary.Count);
        foreach (var (name, id) in vocabulary)
        {
            writer.Write(name);
            write(writer, id);
        }
    }

    private static Dictionary<string, T> Vocabulary<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        var count = ReadCount(reader);
        var vocabulary = new Dictionary<string, T>(count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            vocabulary[name] = read(reader);
        }

        return vocabulary;
    }
}
