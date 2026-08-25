using System.Text;

namespace Maki.Sources.MangaPlus;

/// <summary>
/// A read-only protobuf wire-format decoder, just big enough for the handful of MANGA Plus
/// responses this source reads.
///
/// MANGA Plus used to answer <c>?format=json</c> with JSON; that parameter is now rejected at
/// the edge (nginx 403) and every endpoint serves <c>application/x-protobuf</c> only. Rather
/// than take a Google.Protobuf dependency plus generated code for a schema Shueisha doesn't
/// publish, we walk the wire format directly and pick fields out by number — the wire format
/// is self-describing enough for that, and unknown fields cost nothing.
///
/// Only the wire types the API actually uses are decoded (varint and length-delimited); fixed32
/// and fixed64 are skipped over so an unexpected one can't desync the reader. Groups (wire types
/// 3 and 4) are deprecated and unused here, so they throw.
/// </summary>
internal sealed class PbMessage
{
    private readonly Dictionary<int, List<PbValue>> _fields = [];

    private PbMessage()
    {
    }

    /// <summary>Decodes one message. Throws <see cref="InvalidDataException"/> on malformed input.</summary>
    public static PbMessage Parse(ReadOnlyMemory<byte> data)
    {
        var message = new PbMessage();
        var span = data.Span;
        var offset = 0;

        while (offset < span.Length)
        {
            var tag = ReadVarint(span, ref offset);
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (field == 0)
            {
                throw new InvalidDataException("Protobuf field number 0 is not valid");
            }

            switch (wireType)
            {
                case 0: // varint
                    message.Add(field, new PbValue(ReadVarint(span, ref offset)));
                    break;

                case 2: // length-delimited: string, bytes, or a nested message
                    var length = ReadVarint(span, ref offset);
                    if (length > (ulong)(span.Length - offset))
                    {
                        throw new InvalidDataException("Protobuf length-delimited field runs past the end of the message");
                    }

                    message.Add(field, new PbValue(data.Slice(offset, (int)length)));
                    offset += (int)length;
                    break;

                case 1: // fixed64
                case 5: // fixed32
                    var width = wireType == 1 ? 8 : 4;
                    if (span.Length - offset < width)
                    {
                        throw new InvalidDataException("Protobuf fixed-width field runs past the end of the message");
                    }

                    offset += width;
                    break;

                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wireType}");
            }
        }

        return message;
    }

    /// <summary>The last value of a varint field, or null when the field is absent.</summary>
    public ulong? Number(int field) =>
        Last(field) is { IsBytes: false } value ? value.Number : null;

    /// <summary>The last value of a string field, or null when the field is absent.</summary>
    public string? String(int field) =>
        Last(field) is { IsBytes: true } value ? Encoding.UTF8.GetString(value.Bytes.Span) : null;

    /// <summary>The last value of a nested-message field, or null when the field is absent.</summary>
    public PbMessage? Message(int field) =>
        Last(field) is { IsBytes: true } value ? Parse(value.Bytes) : null;

    /// <summary>Every value of a repeated nested-message field, in wire order.</summary>
    public IEnumerable<PbMessage> Messages(int field)
    {
        if (!_fields.TryGetValue(field, out var values))
        {
            yield break;
        }

        foreach (var value in values)
        {
            if (value.IsBytes)
            {
                yield return Parse(value.Bytes);
            }
        }
    }

    private void Add(int field, PbValue value)
    {
        if (!_fields.TryGetValue(field, out var values))
        {
            _fields[field] = values = [];
        }

        values.Add(value);
    }

    // Protobuf lets a field repeat; for a singular field the last one on the wire wins.
    private PbValue? Last(int field) =>
        _fields.TryGetValue(field, out var values) && values.Count > 0 ? values[^1] : null;

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int offset)
    {
        ulong result = 0;
        var shift = 0;

        while (true)
        {
            if (offset >= span.Length)
            {
                throw new InvalidDataException("Protobuf varint runs past the end of the message");
            }

            if (shift > 63)
            {
                throw new InvalidDataException("Protobuf varint is longer than 64 bits");
            }

            var b = span[offset++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    private readonly struct PbValue
    {
        public PbValue(ulong number)
        {
            Number = number;
            IsBytes = false;
        }

        public PbValue(ReadOnlyMemory<byte> bytes)
        {
            Bytes = bytes;
            IsBytes = true;
        }

        public ulong Number { get; }
        public ReadOnlyMemory<byte> Bytes { get; }
        public bool IsBytes { get; }
    }
}
