using System.Buffers;
using System.Text;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Rewrites a self-contained ONNX model into a graph file plus an external weights file whose
/// tensors start at <see cref="Alignment"/>-byte offsets, which is the layout ONNX Runtime
/// memory-maps instead of copying (see <see cref="MappedEmbeddingModel"/>).
///
/// <para>
/// A protobuf wire-format pass over just the fields involved, so it needs no ONNX schema: the
/// model's graph (field 7), the graph's initializers (field 5), and in each initializer its
/// <c>raw_data</c> (9), <c>external_data</c> (13) and <c>data_location</c> (14). Every other field
/// is copied byte for byte. Only the graph is held in memory; the weights stream from file to file.
/// </para>
/// </summary>
internal static class OnnxExternalData
{
    /// <summary>The Windows allocation granularity, and a multiple of every Linux page size.</summary>
    public const int Alignment = 64 * 1024;

    /// <summary>Smaller initializers stay inline; mapping them would only add padding.</summary>
    public const int MinExternalBytes = 1024;

    private const int ModelGraph = 7;
    private const int GraphInitializer = 5;
    private const int TensorRawData = 9;
    private const int TensorExternalData = 13;
    private const int TensorDataLocation = 14;
    private const int DataLocationExternal = 1;

    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;
    private const int WireFixed32 = 5;

    /// <summary>
    /// Writes the graph to <paramref name="graph"/> and the moved weights to <paramref name="data"/>,
    /// recording <paramref name="dataLocation"/> (a file name relative to the graph) in each moved
    /// tensor. Returns how many initializers moved. Throws <see cref="InvalidDataException"/> on
    /// anything that is not a well-formed model.
    /// </summary>
    public static int Rewrite(Stream source, Stream graph, Stream data, string dataLocation)
    {
        var moved = 0;
        foreach (var field in Scan(source, 0, source.Length))
        {
            if (field is { Number: ModelGraph, Wire: WireLengthDelimited })
            {
                var converted = RewriteGraph(source, field, data, dataLocation, ref moved);
                WriteTag(graph, ModelGraph, WireLengthDelimited);
                WriteVarint(graph, (ulong)converted.Length);
                graph.Write(converted);
            }
            else
            {
                CopyRange(source, field.Start, field.End - field.Start, graph);
            }
        }

        if (moved == 0)
        {
            throw new InvalidDataException("The model has no inline initializers to move");
        }

        return moved;
    }

    private static byte[] RewriteGraph(Stream source, Field graphField, Stream data, string dataLocation, ref int moved)
    {
        using var output = new MemoryStream();
        foreach (var field in Scan(source, graphField.PayloadStart, graphField.End))
        {
            if (field is { Number: GraphInitializer, Wire: WireLengthDelimited } &&
                RewriteTensor(source, field, data, dataLocation) is { } tensor)
            {
                WriteTag(output, GraphInitializer, WireLengthDelimited);
                WriteVarint(output, (ulong)tensor.Length);
                output.Write(tensor);
                moved++;
            }
            else
            {
                CopyRange(source, field.Start, field.End - field.Start, output);
            }
        }

        return output.ToArray();
    }

    /// <summary>The tensor with its raw bytes moved to <paramref name="data"/>, or null to keep it as it is.</summary>
    private static byte[]? RewriteTensor(Stream source, Field tensorField, Stream data, string dataLocation)
    {
        var fields = Scan(source, tensorField.PayloadStart, tensorField.End);
        Field? raw = null;
        foreach (var field in fields)
        {
            if (field.Number is TensorExternalData or TensorDataLocation)
            {
                return null;
            }

            if (field is { Number: TensorRawData, Wire: WireLengthDelimited })
            {
                raw = field;
            }
        }

        if (raw is not { } rawField || rawField.End - rawField.PayloadStart < MinExternalBytes)
        {
            return null;
        }

        var padding = (int)((Alignment - (data.Position % Alignment)) % Alignment);
        if (padding > 0)
        {
            data.Write(new byte[padding]);
        }

        var offset = data.Position;
        var length = rawField.End - rawField.PayloadStart;
        CopyRange(source, rawField.PayloadStart, length, data);

        using var output = new MemoryStream();
        foreach (var field in fields)
        {
            if (field.Number != TensorRawData)
            {
                CopyRange(source, field.Start, field.End - field.Start, output);
            }
        }

        WriteEntry(output, "location", dataLocation);
        WriteEntry(output, "offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteEntry(output, "length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteTag(output, TensorDataLocation, WireVarint);
        WriteVarint(output, DataLocationExternal);
        return output.ToArray();
    }

    /// <summary>A <c>StringStringEntryProto</c> (key 1, value 2) in <c>external_data</c>.</summary>
    private static void WriteEntry(Stream output, string key, string value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        using var entry = new MemoryStream();
        WriteTag(entry, 1, WireLengthDelimited);
        WriteVarint(entry, (ulong)keyBytes.Length);
        entry.Write(keyBytes);
        WriteTag(entry, 2, WireLengthDelimited);
        WriteVarint(entry, (ulong)valueBytes.Length);
        entry.Write(valueBytes);

        WriteTag(output, TensorExternalData, WireLengthDelimited);
        WriteVarint(output, (ulong)entry.Length);
        entry.Position = 0;
        entry.CopyTo(output);
    }

    internal readonly record struct Field(int Number, int Wire, long Start, long PayloadStart, long End);

    /// <summary>The top-level fields of the message in [start, end): positions only, no payloads read.</summary>
    internal static List<Field> Scan(Stream source, long start, long end)
    {
        var fields = new List<Field>();
        var position = start;
        while (position < end)
        {
            source.Position = position;
            var tag = ReadVarint(source);
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (number <= 0)
            {
                throw new InvalidDataException($"Invalid field number at {position}");
            }

            long payloadStart, payloadEnd;
            switch (wire)
            {
                case WireVarint:
                    payloadStart = source.Position;
                    ReadVarint(source);
                    payloadEnd = source.Position;
                    break;
                case WireFixed64:
                    payloadStart = source.Position;
                    payloadEnd = payloadStart + 8;
                    break;
                case WireFixed32:
                    payloadStart = source.Position;
                    payloadEnd = payloadStart + 4;
                    break;
                case WireLengthDelimited:
                    var length = ReadVarint(source);
                    payloadStart = source.Position;
                    if (length > (ulong)(end - payloadStart))
                    {
                        throw new InvalidDataException($"Field {number} at {position} runs past its message");
                    }

                    payloadEnd = payloadStart + (long)length;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported wire type {wire} at {position}");
            }

            if (payloadEnd > end)
            {
                throw new InvalidDataException($"Field {number} at {position} runs past its message");
            }

            fields.Add(new Field(number, wire, position, payloadStart, payloadEnd));
            position = payloadEnd;
        }

        return fields;
    }

    private static ulong ReadVarint(Stream source)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = source.ReadByte();
            if (b < 0)
            {
                throw new InvalidDataException("Truncated varint");
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Varint longer than ten bytes");
    }

    private static void WriteTag(Stream output, int number, int wire) =>
        WriteVarint(output, ((ulong)number << 3) | (uint)wire);

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    private static void CopyRange(Stream source, long start, long length, Stream destination)
    {
        source.Position = start;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 1024 * 1024));
        try
        {
            while (length > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                if (read <= 0)
                {
                    throw new InvalidDataException("The model ended early");
                }

                destination.Write(buffer, 0, read);
                length -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
